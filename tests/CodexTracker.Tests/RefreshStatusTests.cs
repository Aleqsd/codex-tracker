using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class RefreshStatusTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 2)] [InlineData(2, 4)] [InlineData(4, 16)] [InlineData(5, 30)] [InlineData(100, 30)]
    public void RetryBackoffIsBounded(int failures, int minutes) => Assert.Equal(TimeSpan.FromMinutes(minutes), RefreshPolicy.RetryDelay(failures));

    [Theory]
    [InlineData(null, 10)] [InlineData(-1, 10)] [InlineData(0, 2)] [InlineData(299, 2)] [InlineData(300, 10)]
    public void RecentMeasuredConsumptionKeepsTheActiveCadence(int? seconds, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), RefreshPolicy.EffectiveInterval(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2),
            seconds is { } value ? TimeSpan.FromSeconds(value) : null));

    [Fact]
    public async Task AttemptsKeepObservationDatesAndManualSuccessClearsBackoff()
    {
        using var directory = new TestDirectory(); var auth = directory.File("auth.json");
        File.WriteAllBytes(auth, TestFixtures.Auth());
        var snapshot = Snapshot("demo@example.test", "codex");
        var reader = new Reader((_, _, _) => Task.FromResult(snapshot));
        await using var service = new TrackerService(auth, Options(directory) with { DetectClaudeCode = false, AutomaticRefresh = true }, reader);
        await service.InitializeAsync();
        Assert.Equal(RefreshOutcome.Updated, Assert.Single(service.GetRefreshStatus()).Outcome);
        var events = 0; service.Changed += (_, _) => events++;
        await service.RefreshAsync();
        var unchanged = Assert.Single(service.GetRefreshStatus());
        Assert.Equal(RefreshOutcome.Unchanged, unchanged.Outcome);
        Assert.Equal(snapshot.FetchedAt, unchanged.ObservedAt);
        Assert.Equal(2, events); // One coherent start, one coherent completion.
        Assert.NotNull(unchanged.DurationMilliseconds);

        reader.Read = (_, _, _) => throw new TrackerException("fictional-secret-not-for-diagnostics");
        await service.RefreshAsync();
        var failed = Assert.Single(service.GetRefreshStatus());
        Assert.Equal(RefreshOutcome.Failed, failed.Outcome);
        Assert.Equal(snapshot.FetchedAt, failed.ObservedAt);
        Assert.Equal(1, failed.ConsecutiveFailures);
        Assert.True(failed.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.DoesNotContain("fictional-secret", System.Text.Json.JsonSerializer.Serialize(failed));

        reader.Read = (_, _, _) => Task.FromResult(snapshot);
        await service.RefreshAsync();
        var recovered = Assert.Single(service.GetRefreshStatus());
        Assert.Equal(RefreshOutcome.Unchanged, recovered.Outcome);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.ErrorCode);
        await service.SuspendAsync();
        Assert.Equal(RefreshOutcome.Suspended, Assert.Single(service.GetRefreshStatus()).Outcome);
        Assert.Null(Assert.Single(service.GetRefreshStatus()).NextAttemptAt);
    }

    [Fact]
    public async Task OneSlowProviderDoesNotBlockTheOtherProvidersTimer()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var auth = directory.File("auth.json"); File.WriteAllBytes(auth, TestFixtures.Auth());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var codex = new Reader(async (profile, call, token) =>
        {
            if (call == 2) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return Snapshot(profile.Email, "codex");
        });
        var claude = new Reader((profile, _, _) => Task.FromResult(Snapshot(profile.Email, "claude")));
        await using var service = new TrackerService(auth, Options(directory) with { AutomaticRefresh = true, RefreshInterval = TimeSpan.FromMilliseconds(100) }, codex, claude);
        await service.InitializeAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (claude.Calls < 4) await Task.Delay(20, deadline.Token);
            Assert.Equal(2, codex.Calls);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ManualResultDoesNotWaitForTheNextNativeCollection()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var options = Options(directory) with { MonitorAuthChanges = true };
        var signal = new ClaudeCodeObservations(options.DataDirectory, options.ClaudeLocation!).SignalPath;
        Directory.CreateDirectory(Path.GetDirectoryName(signal)!); File.WriteAllText(signal, "0");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader(async (profile, call, token) =>
        {
            if (call == 2) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            if (call == 3) { followUp.TrySetResult(); await finishFollowUp.Task.WaitAsync(token); }
            return Snapshot(profile.Email, "claude");
        });
        await using var service = new TrackerService(directory.File("missing-auth.json"), options, claudeReader: reader);
        await service.InitializeAsync();
        var pending = service.RefreshWithResultAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(signal, "1");
            await Task.Delay(750);
            release.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            await followUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RefreshOutcome.Updated, result.Single(s => s.Provider == AccountProvider.ClaudeCode).Outcome);
            Assert.Equal(RefreshOutcome.Refreshing, service.GetRefreshStatus().Single(s => s.Provider == AccountProvider.ClaudeCode).Outcome);
        }
        finally { release.TrySetResult(); finishFollowUp.TrySetResult(); }
    }

    [Fact]
    public async Task SuspendingAnInFlightAttemptReturnsSuspendedWithoutInventingFailure()
    {
        using var directory = new TestDirectory(); var auth = directory.File("auth.json"); File.WriteAllBytes(auth, TestFixtures.Auth());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader(async (profile, call, token) =>
        {
            if (call == 2) { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Snapshot(profile.Email, "codex");
        });
        await using var service = new TrackerService(auth, Options(directory) with { DetectClaudeCode = false }, reader);
        await service.InitializeAsync();
        var result = service.RefreshWithResultAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.SuspendAsync();
        var stopped = Assert.Single(await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(RefreshOutcome.Suspended, stopped.Outcome);
        Assert.Equal(0, stopped.ConsecutiveFailures);
        Assert.Null(stopped.NextAttemptAt);
    }

    [Fact]
    public async Task SameClaudeIdentityReceivesUpdatedOrganizationMetadata()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory, organizationName: "Before", organizationType: "claude_team");
        var claude = new Reader((profile, _, _) => Task.FromResult(Snapshot(profile.Email, "claude")));
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory), claudeReader: claude);
        await service.InitializeAsync();
        var profile = service.State.ActiveAccount!.Profile;
        ClaudeFixture.SignIn(directory, organizationName: "After", organizationType: "claude_team");
        await service.RefreshAsync();
        Assert.Equal(profile.Id, service.State.ActiveAccount!.Profile.Id);
        Assert.Equal("After", service.State.ActiveAccount.Profile.OrganizationName);
        Assert.Equal(RefreshOutcome.Unavailable, service.GetRefreshStatus().Single(s => s.Provider == AccountProvider.Codex).Outcome);
    }

    private static TrackerServiceOptions Options(TestDirectory directory) => new()
    {
        DataDirectory = directory.File("data"), AutomaticRefresh = false, MonitorAuthChanges = false,
        ClaudeLocation = ClaudeFixture.Location(directory), ClaudeDesktopUsagePaths = [], RefreshInterval = TimeSpan.FromSeconds(30)
    };
    private static AccountSnapshot Snapshot(string email, string provider) => new(email, "pro",
        [new(provider, provider, [new(20, 10080, DateTimeOffset.UtcNow.AddDays(2))])], null, null, DateTimeOffset.UtcNow);
    private sealed class Reader(Func<AccountProfile, int, CancellationToken, Task<AccountSnapshot>> read) : IAccountUsageReader
    {
        public Func<AccountProfile, int, CancellationToken, Task<AccountSnapshot>> Read = read;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken token) => Read(profile, Interlocked.Increment(ref _calls), token);
    }
}
