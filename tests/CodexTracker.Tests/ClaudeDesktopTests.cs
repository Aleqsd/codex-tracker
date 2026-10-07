using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeDesktopTests
{
    private static readonly Guid Organization = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Work = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T08:00:00Z");
    private static readonly ClaudeCodeIdentity Identity = new("demo@example.test", ClaudeFixture.First + "/" + Organization, "team", null, 5, "Fictional team");
    private static string History(DateTimeOffset at, Guid organization, object usage, int version = 2) =>
        JsonSerializer.Serialize(new { version, samples = new[] { new { t = at.ToUnixTimeMilliseconds(), org = organization.ToString(), u = usage } } });
    private static AccountSnapshot? Parse(string json) { using var document = JsonDocument.Parse(json); return ClaudeDesktopUsageReader.Parse(document.RootElement, Identity, Organization, Now); }

    [Fact]
    public void NativeCacheKeepsItsDateAndSelectsOnlyTheCorrectOrganization()
    {
        var at = Now.AddMinutes(-15);
        var snapshot = Parse(History(at, Organization, new { fh = 2, sd = 28, xu = 0 }))!;
        Assert.Equal(98, snapshot.Short!.RemainingPercent); Assert.Equal(72, snapshot.Weekly!.RemainingPercent);
        Assert.Equal(at, snapshot.FetchedAt); Assert.Null(snapshot.Short.ResetsAt); Assert.Null(snapshot.Weekly.ResetsAt); Assert.Null(snapshot.AvailableResetCredits);
        Assert.Null(Parse(History(at, Work, new { fh = 50, sd = 60 })));
        Assert.Null(Parse(History(at, Organization, new { fh = 2, sd = 28 }, version: 1)));
        Assert.Null(Parse(History(at, Organization, new { fh = 2, sd = 28 }, version: 3)));
        Assert.Null(Parse(History(Now.AddMinutes(1), Organization, new { fh = 2 })));
        Assert.Null(Parse(History(Now.AddDays(-31), Organization, new { fh = 2 })));
        Assert.Null(Parse(History(at, Organization, new { sn = 2, xu = 0 })));
        var shortOnly = Parse(History(at, Organization, new { fh = 2 }))!;
        Assert.Null(shortOnly.Weekly); Assert.Equal(98, shortOnly.Short!.RemainingPercent);
    }

    [Theory]
    [InlineData(-1)] [InlineData(101)] [InlineData(1e300)]
    public void InvalidDesktopPercentageCannotBecomeAQuota(double invalid) =>
        Assert.Null(Parse(History(Now, Organization, new { fh = invalid, sd = 28 })));

    [Fact]
    public void NewerEmptySampleCannotReanimateAnOlderQuota()
    {
        var json = JsonSerializer.Serialize(new { version = 2, samples = new object[] {
            new { t = Now.AddMinutes(-2).ToUnixTimeMilliseconds(), org = Organization, u = new { fh = 50, sd = 60 } },
            new { t = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), org = Organization, u = new { } } } });
        Assert.Null(Parse(json));
    }

    [Fact]
    public async Task DesktopUsageWorksWithoutStandaloneCredentialAndNeverRedatesOrModifiesNativeFiles()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory, organizationName: "Fictional team", organizationType: "claude_team");
        File.WriteAllText(directory.File(".credentials.json"), "{\"mcpOAuth\":{\"fixture\":\"fixture-secret-never-persist\"}}");
        var path = directory.File("plan-usage-history.json"); var observedAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        File.WriteAllText(path, History(observedAt, Organization, new { fh = 2, sd = 28 }));
        var metadata = File.ReadAllBytes(directory.File(".claude.json")); var credentials = File.ReadAllBytes(directory.File(".credentials.json")); var history = File.ReadAllBytes(path);
        var options = new TrackerServiceOptions { DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory),
            ClaudeDesktopUsagePaths = [path], AutomaticRefresh = false, MonitorAuthChanges = false };
        await using var service = new TrackerService(directory.File("missing-auth.json"), options);
        await service.InitializeAsync(); var account = Assert.Single(service.State.Accounts);
        Assert.True(account.IsActiveInClaudeCode); Assert.True(account.IsConnected); Assert.Equal("Fictional team", account.Profile.OrganizationName);
        Assert.Equal("team", account.Profile.ProviderPlanType); Assert.Equal(98, account.Snapshot!.Short!.RemainingPercent); Assert.Equal(72, account.Snapshot.Weekly!.RemainingPercent);
        Assert.Equal(observedAt.ToUnixTimeMilliseconds(), account.Snapshot.FetchedAt.ToUnixTimeMilliseconds());
        await service.RefreshAsync(); Assert.Equal(account.Snapshot.FetchedAt, service.State.ActiveAccount!.Snapshot!.FetchedAt);
        Assert.Equal(metadata, File.ReadAllBytes(directory.File(".claude.json"))); Assert.Equal(credentials, File.ReadAllBytes(directory.File(".credentials.json"))); Assert.Equal(history, File.ReadAllBytes(path));
        Assert.DoesNotContain("fixture-secret", File.ReadAllText(Path.Combine(options.DataDirectory, "settings.json")));
    }

    [Fact]
    public async Task PersonalAndWorkOnTheSameEmailStaySeparateThroughSwitchingAndRestart()
    {
        using var directory = new TestDirectory(); var email = "same@example.test";
        var path = directory.File("plan-usage-history.json");
        var options = new TrackerServiceOptions { DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory),
            ClaudeDesktopUsagePaths = [path], AutomaticRefresh = false, MonitorAuthChanges = false };
        ClaudeFixture.SignIn(directory, email, organizationName: "Personnel");
        File.WriteAllText(path, History(DateTimeOffset.UtcNow.AddMinutes(-5), Organization, new { fh = 40, sd = 30 }));
        Guid personal, work;
        await using (var service = new TrackerService(directory.File("missing-auth.json"), options))
        {
            await service.InitializeAsync(); var original = service.State.ActiveAccount!; personal = original.Profile.Id;
            ClaudeFixture.SignIn(directory, email, organization: Work.ToString(), organizationName: "Fictional company", organizationType: "claude_team");
            File.WriteAllText(path, History(DateTimeOffset.UtcNow.AddMinutes(-2), Work, new { fh = 10, sd = 20 }));
            await service.RefreshAsync(); var selected = service.State.ActiveAccount!; work = selected.Profile.Id;
            Assert.NotEqual(personal, work); Assert.Equal(2, service.State.Accounts.Count);
            Assert.Equal(80, selected.Snapshot!.Weekly!.RemainingPercent);
            var retained = service.State.Accounts.Single(a => a.Profile.Id == personal);
            Assert.False(retained.IsActive); Assert.Equal(original.Snapshot, retained.Snapshot);
            Assert.NotEqual(retained.Profile.ProviderAccountId, selected.Profile.ProviderAccountId);
            ClaudeFixture.SignIn(directory, email, organizationName: "Personnel");
            await service.RefreshAsync(); Assert.Equal(personal, service.State.ActiveAccount!.Profile.Id); Assert.Equal(original.Snapshot, service.State.ActiveAccount.Snapshot);
        }
        await using var restarted = new TrackerService(directory.File("missing-auth.json"), options); await restarted.InitializeAsync();
        Assert.Equal(new[] { personal, work }, restarted.State.Accounts.Select(a => a.Profile.Id));
        Assert.Equal(personal, restarted.State.ActiveAccount!.Profile.Id);
    }
}
