using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class TrackerServiceTests
{
    [Fact]
    public async Task FailedRefreshRetainsLastSnapshotAndMarksItStale()
    {
        using var directory = new TestDirectory();
        var profile = new AccountProfile(Guid.NewGuid(), "demo@example.test");
        var fetchedAt = DateTimeOffset.UtcNow;
        var snapshot = new AccountSnapshot(profile.Email, "plus",
            [new("codex", "Codex", [new(42, 10080, fetchedAt.AddDays(2))])],
            3, null, fetchedAt);
        using (var seed = new ProfileStore(directory.Root))
        {
            seed.Open();
            seed.SaveSettings(new([profile], profile.Id, true));
            seed.SaveAuth(profile.Id, TestFixtures.Auth());
            seed.SaveSnapshots(new() { [profile.Id] = snapshot });
        }

        await using var service = new TrackerService(new NoDesktop(directory.File("absent-auth.json")), new()
        {
            DataDirectory = directory.Root,
            CodexExecutablePath = directory.File("does-not-exist-codex.exe"),
            AutomaticRefresh = false
        });
        await service.InitializeAsync();
        var account = Assert.Single(service.State.Accounts);
        Assert.NotNull(account.Snapshot);
        Assert.Equal(fetchedAt, account.Snapshot.FetchedAt);
        Assert.Equal(58, account.Snapshot.Weekly!.RemainingPercent);
        Assert.Equal(3, account.Snapshot.AvailableResetCredits);
        Assert.NotNull(account.Error);
        Assert.True(account.IsStale);
        Assert.False(account.IsRefreshing);
        Assert.False(service.State.IsBusy);
        Assert.Empty(Directory.EnumerateDirectories(directory.File("runtime")));

        await service.RefreshAsync();
        var again = Assert.Single(service.State.Accounts);
        Assert.Same(account.Snapshot, again.Snapshot);
        Assert.NotNull(again.Error);
    }

    [Fact]
    public async Task SwitchRereadsDesktopIdentityBeforeSkippingAnApparentlyActiveAccount()
    {
        using var directory = new TestDirectory();
        var desktop = new MutableDesktop(directory.File("auth.json"));
        var requestedAuth = TestFixtures.Auth();
        await File.WriteAllBytesAsync(desktop.AuthFilePath, requestedAuth);
        await using var service = new TrackerService(desktop, FixtureOptions(directory));
        await service.InitializeAsync();
        var requested = Assert.Single(service.State.Accounts);
        Assert.True(requested.IsActiveInCodex);

        // Codex changes independently between the timer tick and the user's switch click.
        await File.WriteAllBytesAsync(desktop.AuthFilePath, TestFixtures.Auth("other@example.test", "other"));
        var result = await service.SwitchAccountAsync(requested.Profile.Id, true);

        Assert.Equal(1, desktop.ActivationCalls);
        Assert.Equal("demo@example.test", desktop.LastRequestedEmail);
        Assert.True(result.NeedsUserVerification);
        Assert.Equal(requestedAuth, await File.ReadAllBytesAsync(desktop.AuthFilePath));
        Assert.Equal("demo@example.test", service.State.PendingSwitchEmail);
        Assert.False(service.State.IsBusy);
    }

    [Fact]
    public async Task MalformedCurrentAuthCannotPreventRestoringTheJournaledSession()
    {
        using var directory = new TestDirectory();
        var previous = TestFixtures.Auth("before@example.test", "journaled");
        var desktop = new MutableDesktop(directory.File("auth.json")) { RestoreAuth = previous };
        await File.WriteAllBytesAsync(desktop.AuthFilePath, TestFixtures.Auth());
        await using var service = new TrackerService(desktop, FixtureOptions(directory));
        await service.InitializeAsync();
        desktop.PendingTargetEmail = "demo@example.test";
        await File.WriteAllTextAsync(desktop.AuthFilePath, "{ malformed current credentials");

        var result = await service.ConfirmSwitchAsync(false);

        Assert.Equal(1, desktop.ConfirmationCalls);
        Assert.True(result.Success);
        Assert.False(result.NeedsUserVerification);
        Assert.Equal(previous, await File.ReadAllBytesAsync(desktop.AuthFilePath));
        Assert.Null(service.State.PendingSwitchEmail);
        Assert.Equal("before@example.test", Assert.Single(service.State.Accounts, a => a.IsActiveInCodex).Profile.Email);
        Assert.False(service.State.IsBusy);
    }

    private static TrackerServiceOptions FixtureOptions(TestDirectory directory) => new()
    {
        DataDirectory = directory.File("data"),
        CodexExecutablePath = directory.File("does-not-exist-codex.exe"),
        AutomaticRefresh = false
    };

    private sealed class MutableDesktop(string path) : IDesktopSessionManager
    {
        public string AuthFilePath => path;
        public string? PendingTargetEmail { get; set; }
        public byte[]? RestoreAuth { get; init; }
        public int ActivationCalls { get; private set; }
        public int ConfirmationCalls { get; private set; }
        public string? LastRequestedEmail { get; private set; }
        public Task<DesktopAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopAvailability(true, true, null, DesktopSessionManager.SupportedDesktopVersion));
        public async Task<DesktopActivationResult> ActivateAsync(byte[] targetAuthJson, string expectedEmail,
            bool confirmed, CancellationToken cancellationToken = default)
        {
            ActivationCalls++;
            LastRequestedEmail = expectedEmail;
            var previous = await File.ReadAllBytesAsync(AuthFilePath, cancellationToken);
            await File.WriteAllBytesAsync(AuthFilePath, targetAuthJson, cancellationToken);
            PendingTargetEmail = expectedEmail;
            return new(false, "Fixture: verify target account", true, previous);
        }
        public async Task<DesktopActivationResult> ConfirmActivationAsync(bool accepted,
            CancellationToken cancellationToken = default)
        {
            ConfirmationCalls++;
            Assert.False(accepted);
            var displaced = await File.ReadAllBytesAsync(AuthFilePath, cancellationToken);
            await File.WriteAllBytesAsync(AuthFilePath, RestoreAuth!, cancellationToken);
            PendingTargetEmail = null;
            return new(true, "Fixture: session restored", PreviousAuthJson: displaced);
        }
    }

    private sealed class NoDesktop(string path) : IDesktopSessionManager
    {
        public string AuthFilePath => path;
        public string? PendingTargetEmail => null;
        public Task<DesktopAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DesktopAvailability(false, false, "Fixture: desktop absent", null));
        public Task<DesktopActivationResult> ActivateAsync(byte[] targetAuthJson, string expectedEmail,
            bool confirmed, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesktopActivationResult> ConfirmActivationAsync(bool accepted,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
