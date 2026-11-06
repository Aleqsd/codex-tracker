using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeDesktopSelectionTests
{
    private static readonly Guid Work = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Personal = Guid.Parse("55555555-5555-4555-8555-555555555555");

    private static string[] Paths(TestDirectory directory) => [directory.File("plan-usage-history.json")];

    private static string Session(TestDirectory directory, Guid account, Guid organization, DateTimeOffset at, string? name = null)
    {
        var path = directory.File(Path.Combine("claude-code-sessions", account.ToString(), organization.ToString(), (name ?? "local_" + Guid.NewGuid()) + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { cwd = "fixture-private-project-never-persist", title = "fixture-private-chat-never-persist" }));
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
        return path;
    }

    private static void Agent(TestDirectory directory, Guid account, Guid organization, string email, string? organizationType = null, string? organizationName = null)
    {
        var path = directory.File(Path.Combine("local-agent-mode-sessions", account.ToString(), organization.ToString(), "local_" + Guid.NewGuid(), ".claude", ".claude.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = email, accountUuid = account.ToString(),
            organizationUuid = organization.ToString(), organizationType, organizationName } }));
    }

    // The terminal login was confirmed two hours ago; a Desktop Code session written since then designates the account in use.
    private static void SignInEarlier(TestDirectory directory, string email, Guid? id = null, Guid? organization = null, string? organizationName = null, string? organizationType = null)
    {
        ClaudeFixture.SignIn(directory, email, id, organization: (organization ?? Work).ToString(), organizationName: organizationName, organizationType: organizationType);
        File.SetLastWriteTimeUtc(directory.File(".credentials.json"), DateTime.UtcNow.AddHours(-2));
    }

    private static Task<ClaudeCodeIdentity> Selected(TestDirectory directory, params AccountProfile[] known) =>
        ClaudeCodeIdentity.ReadSelectedAsync(ClaudeFixture.Location(directory), Paths(directory), known, default);

    [Fact]
    public async Task ANewerDesktopSessionOnAnotherAccountWinsUntilTheTerminalIsConfirmedAgain()
    {
        using var directory = new TestDirectory();
        SignInEarlier(directory, "work@example.test", organizationName: "Fictional company", organizationType: "claude_team");
        Agent(directory, ClaudeFixture.Second, Personal, "personal@example.test", "claude_max");
        var session = Session(directory, ClaudeFixture.Second, Personal, DateTimeOffset.UtcNow.AddMinutes(-1));
        var metadata = File.ReadAllBytes(directory.File(".claude.json")); var native = File.ReadAllBytes(session);

        var identity = await Selected(directory);
        Assert.Equal("personal@example.test", identity.Email); Assert.Equal(ClaudeFixture.Second + "/" + Personal, identity.AccountId);
        Assert.Equal("max", identity.PlanType); Assert.Equal("Personnel", identity.OrganizationName); Assert.Null(identity.PlanMultiplier);
        Assert.False(identity.HasLocalCredential); Assert.Null(identity.ExpiresAt);
        Assert.Equal(metadata, File.ReadAllBytes(directory.File(".claude.json"))); Assert.Equal(native, File.ReadAllBytes(session));

        // A newer terminal login, or a newer profile fetch by the CLI, makes the terminal account current again.
        ClaudeFixture.SignIn(directory, "work@example.test", organization: Work.ToString());
        Assert.Equal("work@example.test", (await Selected(directory)).Email);
        File.SetLastWriteTimeUtc(directory.File(".credentials.json"), DateTime.UtcNow.AddHours(-2));
        Assert.Equal("personal@example.test", (await Selected(directory)).Email);
        File.WriteAllText(directory.File(".claude.json"), JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = "work@example.test",
            accountUuid = ClaudeFixture.First.ToString(), organizationUuid = Work.ToString(), profileFetchedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } }));
        Assert.Equal("work@example.test", (await Selected(directory)).Email);
    }

    [Fact]
    public async Task DesktopCachedProfileGivesThePlanOfTheSelectedOrganizationOnly()
    {
        using var cache = new ClaudeDesktopCacheTests.CacheFixture(); var directory = cache.Directory; var now = DateTimeOffset.UtcNow;
        SignInEarlier(directory, "work@example.test", organizationName: "Fictional company", organizationType: "claude_team");
        Session(directory, ClaudeFixture.Second, Personal, now.AddMinutes(-1));
        static string Profile(Guid organization, string type = "claude_max") => JsonSerializer.Serialize(new
        {
            account = new { uuid = ClaudeFixture.Second, email = "personal@example.test", full_name = "fixture-private-name", has_claude_max = true },
            organization = new { uuid = organization, name = "personal@example.test's Organization", organization_type = type, rate_limit_tier = "default_claude_max_20x" }
        });
        const string endpoint = "https://api.anthropic.com/api/oauth/profile";
        cache.Add(Guid.Empty, now.AddMinutes(-30), Profile(Personal), "br", url: endpoint);
        cache.Add(Guid.Empty, now.AddMinutes(-20), Profile(Personal, "claude_pro"), url: "https://claude.ai/api/oauth/profile");
        var files = cache.Files();
        // A profile already followed without its plan, as created by 0.9.28, receives it.
        var known = new AccountProfile(Guid.NewGuid(), "personal@example.test", AccountProvider.ClaudeCode, ClaudeFixture.Second + "/" + Personal);
        var identity = await Selected(directory, known);
        Assert.Equal("personal@example.test", identity.Email); Assert.Equal("max", identity.PlanType); Assert.Equal(20, identity.PlanMultiplier);
        Assert.Equal("Personnel", identity.OrganizationName); Assert.Equal(files, cache.Files());

        // A newer profile of another organization cannot describe the selected one.
        cache.Add(Guid.Empty, now.AddMinutes(-10), Profile(Work), "zstd", url: endpoint);
        identity = await Selected(directory, known);
        Assert.Equal("personal@example.test", identity.Email); Assert.Null(identity.PlanType); Assert.Null(identity.PlanMultiplier);
    }

    [Fact]
    public async Task OnlyTheDefaultNameOfAPersonalOrganizationIsReplaced()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory, organizationName: "demo@example.test's Organization", organizationType: "claude_max");
        Assert.Equal("Personnel", (await ClaudeCodeIdentity.ReadAsync(ClaudeFixture.Location(directory), default)).OrganizationName);
        ClaudeFixture.SignIn(directory, organizationName: "other@example.test's Organization", organizationType: "claude_max");
        Assert.Equal("other@example.test's Organization", (await ClaudeCodeIdentity.ReadAsync(ClaudeFixture.Location(directory), default)).OrganizationName);
        // A team organization keeps any name; without a terminal login the plan comes from its type.
        ClaudeFixture.SignIn(directory, organizationName: "demo@example.test's Organization", organizationType: "claude_team");
        File.WriteAllText(directory.File(".credentials.json"), "{}");
        Assert.Equal("demo@example.test's Organization", (await ClaudeCodeIdentity.ReadAsync(ClaudeFixture.Location(directory), default)).OrganizationName);
    }

    [Fact]
    public void TheRefreshedProfileNamesThePlanBeforeTheCredentialOfItsLogin()
    {
        // A Max 20x personal organization whose credential still says team / 5x: the address is not repeated as its name.
        using var profile = JsonDocument.Parse("""{"emailAddress":"demo@example.test","accountUuid":"11111111-1111-4111-8111-111111111111","organizationUuid":"33333333-3333-4333-8333-333333333333","organizationName":"demo@example.test's Organization","organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_20x","userRateLimitTier":null}""");
        using var credential = JsonDocument.Parse("""{"subscriptionType":"team","rateLimitTier":"default_claude_max_5x"}""");
        var identity = ClaudeCodeIdentity.FromMetadata(profile.RootElement, credential.RootElement);
        Assert.Equal("max", identity.PlanType); Assert.Equal(20, identity.PlanMultiplier); Assert.Equal("Personnel", identity.OrganizationName);
        // A seat tier given by the profile comes before the organization's; the credential only fills what the profile omits.
        using var seat = JsonDocument.Parse("""{"emailAddress":"demo@example.test","accountUuid":"11111111-1111-4111-8111-111111111111","organizationType":"claude_team","organizationRateLimitTier":"default_claude_max_20x","userRateLimitTier":"default_claude_max_5x"}""");
        var member = ClaudeCodeIdentity.FromMetadata(seat.RootElement, default);
        Assert.Equal("team", member.PlanType); Assert.Equal(5, member.PlanMultiplier);
        using var bare = JsonDocument.Parse("""{"emailAddress":"demo@example.test","accountUuid":"11111111-1111-4111-8111-111111111111"}""");
        var fallback = ClaudeCodeIdentity.FromMetadata(bare.RootElement, credential.RootElement);
        Assert.Equal("team", fallback.PlanType); Assert.Equal(5, fallback.PlanMultiplier);
    }

    [Fact]
    public async Task DesktopAloneNamesItsAccountAndMissingSourcesKeepTheOriginalError()
    {
        using var directory = new TestDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() => Selected(directory));
        Agent(directory, ClaudeFixture.Second, Personal, "personal@example.test");
        Session(directory, ClaudeFixture.Second, Personal, DateTimeOffset.UtcNow.AddMinutes(-1));
        var identity = await Selected(directory);
        Assert.Equal("personal@example.test", identity.Email); Assert.Null(identity.PlanType); Assert.Null(identity.OrganizationName);
    }

    [Fact]
    public async Task SameAccountInAnotherOrganizationKeepsTheAddressButNeverTheTerminalOrganization()
    {
        using var directory = new TestDirectory();
        SignInEarlier(directory, "same@example.test", ClaudeFixture.First, Work, "Fictional company", "claude_team");
        Session(directory, ClaudeFixture.First, Personal, DateTimeOffset.UtcNow.AddMinutes(-1));
        var identity = await Selected(directory);
        Assert.Equal("same@example.test", identity.Email); Assert.Equal(ClaudeFixture.First + "/" + Personal, identity.AccountId);
        Assert.Null(identity.OrganizationName); Assert.Null(identity.PlanType); Assert.Null(identity.PlanMultiplier);

        var known = new AccountProfile(Guid.NewGuid(), "same@example.test", AccountProvider.ClaudeCode, ClaudeFixture.First + "/" + Personal, "Personnel", "max", 20);
        identity = await Selected(directory, known);
        Assert.Equal("Personnel", identity.OrganizationName); Assert.Equal("max", identity.PlanType); Assert.Equal(20, identity.PlanMultiplier);
        // The same Desktop session as the terminal changes nothing.
        Session(directory, ClaudeFixture.First, Work, DateTimeOffset.UtcNow);
        Assert.Equal("Fictional company", (await Selected(directory, known)).OrganizationName);
    }

    [Fact]
    public async Task AnUnknownDesktopAccountIsReportedInsteadOfKeepingTheTerminalOne()
    {
        using var directory = new TestDirectory();
        SignInEarlier(directory, "work@example.test");
        var options = new TrackerServiceOptions { DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory),
            ClaudeDesktopUsagePaths = Paths(directory), AutomaticRefresh = false, MonitorAuthChanges = false };
        await using var service = new TrackerService(directory.File("missing-auth.json"), options);
        await service.InitializeAsync();
        var work = Assert.Single(service.State.ActiveAccounts);

        Session(directory, ClaudeFixture.Second, Personal, DateTimeOffset.UtcNow.AddMinutes(-1));
        // An agent configuration of another account cannot name it.
        Agent(directory, ClaudeFixture.First, Personal, "work@example.test");
        var error = await Assert.ThrowsAsync<TrackerException>(() => Selected(directory));
        Assert.Contains("Claude Desktop", error.Message); Assert.DoesNotContain("example.test", error.Message);
        await service.RefreshAsync();
        Assert.Empty(service.State.ActiveAccounts); Assert.Equal(error.Message, service.State.StatusMessage);
        Assert.Equal(work.Profile, Assert.Single(service.State.Accounts).Profile);
    }

    [Fact]
    public void OnlyRecentCodeSessionsInAccountAndOrganizationFoldersCount()
    {
        using var directory = new TestDirectory(); var now = DateTimeOffset.UtcNow;
        Assert.Null(ClaudeDesktopSelection.Read(Paths(directory), now, default));
        Session(directory, ClaudeFixture.First, Work, now.AddMinutes(-30));
        Session(directory, ClaudeFixture.Second, Personal, now.AddMinutes(-1), "scheduled-tasks");
        Session(directory, ClaudeFixture.Second, Personal, now.AddMinutes(-1), "local_not-a-session");
        Session(directory, ClaudeFixture.Second, Personal, now.AddMinutes(10));
        Session(directory, ClaudeFixture.Second, Work, now.AddDays(-31));
        Session(directory, Guid.Empty, Personal, now.AddMinutes(-1));
        var invalid = directory.File(Path.Combine("claude-code-sessions", "fixture", Personal.ToString(), "local_" + Guid.NewGuid() + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(invalid)!); File.WriteAllText(invalid, "{}");
        var selection = ClaudeDesktopSelection.Read(Paths(directory), now, default)!;
        Assert.Equal(ClaudeFixture.First, selection.Account); Assert.Equal(Work, selection.Organization);
        Assert.InRange(selection.ActiveAt, now.AddMinutes(-30).AddSeconds(-1), now.AddMinutes(-30).AddSeconds(1));
    }

    [Fact]
    public void LargeSessionDirectoriesProgressPastTheFirstBatchAndRevalidateTheirCandidate()
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1025; i++) Session(directory, ClaudeFixture.Second, Personal, now.AddDays(-31));
        var sessions = directory.File($"claude-code-sessions/{ClaudeFixture.Second}/{Personal}");
        var outsideFirstBatch = Directory.EnumerateFiles(sessions, "local_*.json").Skip(1024).First();
        File.SetLastWriteTimeUtc(outsideFirstBatch, now.AddMinutes(-1).UtcDateTime);
        try
        {
            Assert.Null(ClaudeDesktopSelection.Read(Paths(directory), now, default));
            var selected = ClaudeDesktopSelection.Read(Paths(directory), now, default);
            Assert.NotNull(selected);
            Assert.Equal(ClaudeFixture.Second, selected.Account);
            Assert.Equal(Personal, selected.Organization);

            // Even while another bounded sweep is incomplete, a deleted or aged cached candidate
            // must never keep its old organization selected.
            File.SetLastWriteTimeUtc(outsideFirstBatch, now.AddDays(-31).UtcDateTime);
            Assert.Null(ClaudeDesktopSelection.Read(Paths(directory), now, default));
            File.SetLastWriteTimeUtc(outsideFirstBatch, now.AddMinutes(-1).UtcDateTime);
            Assert.NotNull(ClaudeDesktopSelection.Read(Paths(directory), now, default));
            File.Delete(outsideFirstBatch);
            Assert.Null(ClaudeDesktopSelection.Read(Paths(directory), now, default));
        }
        finally { ClaudeDesktopSelection.Release(Paths(directory)); }
    }

    [Theory]
    [InlineData(10, true)] [InlineData(30, true)] [InlineData(65, false)]
    public void ProgressiveSessionScanSurvivesAdaptiveAndRetryIntervalsButStillExpires(int idleMinutes, bool resumes)
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1025; i++) Session(directory, ClaudeFixture.Second, Personal, now.AddDays(-31));
        var sessions = directory.File($"claude-code-sessions/{ClaudeFixture.Second}/{Personal}");
        var beyondFirstBatch = Directory.EnumerateFiles(sessions, "local_*.json").Skip(1024).First();
        File.SetLastWriteTimeUtc(beyondFirstBatch, now.AddMinutes(-1).UtcDateTime);
        try
        {
            Assert.Null(ClaudeDesktopSelection.Read(Paths(directory), now, default));
            AgeMetadataCursor(sessions, TimeSpan.FromMinutes(idleMinutes));
            var next = ClaudeDesktopSelection.Read(Paths(directory), now, default);
            if (!resumes)
            {
                Assert.Null(next); // An hour of inactivity releases the old enumeration.
                next = ClaudeDesktopSelection.Read(Paths(directory), now, default);
            }
            Assert.NotNull(next);
            Assert.Equal(ClaudeFixture.Second, next.Account);
            Assert.Equal(Personal, next.Organization);
        }
        finally { ClaudeDesktopSelection.Release(Paths(directory)); }
    }

    [Theory]
    [InlineData(10)] [InlineData(30)]
    public async Task LargeAgentDirectoriesEventuallyReadConfigurationsBeyondTheFirstBatch(int idleMinutes)
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.UtcNow;
        var root = Directory.CreateDirectory(directory.File($"local-agent-mode-sessions/{ClaudeFixture.Second}/{Personal}"));
        for (var i = 0; i < 257; i++) Directory.CreateDirectory(Path.Combine(root.FullName, "local_" + Guid.NewGuid()));
        var outsideFirstBatch = Directory.EnumerateDirectories(root.FullName, "local_*").Skip(256).First();
        var config = Path.Combine(Directory.CreateDirectory(Path.Combine(outsideFirstBatch, ".claude")).FullName, ".claude.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = "later@example.test",
            accountUuid = ClaudeFixture.Second, organizationUuid = Personal, organizationType = "claude_pro" } }));
        Directory.SetLastWriteTimeUtc(outsideFirstBatch, now.UtcDateTime.AddSeconds(1));
        var selection = new ClaudeDesktopSelection.Selection(ClaudeFixture.Second, Personal, now);
        try
        {
            Assert.Null(await ClaudeDesktopSelection.ReadAgentIdentityAsync(Paths(directory), selection, default));
            AgeMetadataCursor(root.FullName, TimeSpan.FromMinutes(idleMinutes));
            var identity = await ClaudeDesktopSelection.ReadAgentIdentityAsync(Paths(directory), selection, default);
            Assert.NotNull(identity);
            Assert.Equal("later@example.test", identity.Email);
            Assert.Equal(ClaudeFixture.Second + "/" + Personal, identity.AccountId);
            File.Delete(config);
            Assert.Null(await ClaudeDesktopSelection.ReadAgentIdentityAsync(Paths(directory), selection, default));
        }
        finally { ClaudeDesktopSelection.Release(Paths(directory)); }
    }

    private static void AgeMetadataCursor(string directory, TimeSpan idle)
    {
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.NonPublic;
        var scan = typeof(ClaudeSessionMetadataScan);
        var sync = scan.GetField("Sync", fields | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        lock (sync)
        {
            // Age only this fixture's cursor. A global fake clock could expire cursors belonging
            // to other tests that run in parallel, and waiting ten real minutes is unnecessary.
            var cursors = (System.Collections.IDictionary)scan.GetField("Cursors", fields | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var cursor = cursors[Path.GetFullPath(directory)];
            Assert.NotNull(cursor);
            var lastUsed = cursor.GetType().GetField("LastUsed", fields | System.Reflection.BindingFlags.Instance)!;
            lastUsed.SetValue(cursor, System.Diagnostics.Stopwatch.GetTimestamp() -
                checked((long)(idle.TotalSeconds * System.Diagnostics.Stopwatch.Frequency)));
        }
    }

    [Fact]
    public async Task TrackerFollowsADesktopSwitchWithItsOwnQuotasAndKeepsTheOtherAccount()
    {
        using var directory = new TestDirectory(); var observedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        SignInEarlier(directory, "work@example.test", organizationName: "Fictional company", organizationType: "claude_team");
        Agent(directory, ClaudeFixture.Second, Personal, "personal@example.test", "claude_pro");
        var history = directory.File("plan-usage-history.json");
        File.WriteAllText(history, JsonSerializer.Serialize(new { version = 2, samples = new object[] {
            new { t = observedAt.ToUnixTimeMilliseconds(), org = Work.ToString(), u = new { fh = 10, sd = 20 } },
            new { t = observedAt.ToUnixTimeMilliseconds(), org = Personal.ToString(), u = new { fh = 40, sd = 70 } } } }));
        var options = new TrackerServiceOptions { DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory),
            ClaudeDesktopUsagePaths = [history], AutomaticRefresh = false, MonitorAuthChanges = false };
        Guid work, personal;
        await using (var service = new TrackerService(directory.File("missing-auth.json"), options))
        {
            await service.InitializeAsync();
            var first = service.State.ActiveAccount!; work = first.Profile.Id;
            Assert.Equal("work@example.test", first.Profile.Email); Assert.Equal(80, first.Snapshot!.Weekly!.RemainingPercent);

            Session(directory, ClaudeFixture.Second, Personal, DateTimeOffset.UtcNow.AddMinutes(-1));
            await service.RefreshAsync();
            var selected = Assert.Single(service.State.ActiveAccounts); personal = selected.Profile.Id;
            Assert.Equal("personal@example.test", selected.Profile.Email); Assert.True(selected.IsActiveInClaudeCode);
            Assert.Equal(ClaudeFixture.Second + "/" + Personal, selected.Profile.ProviderAccountId);
            Assert.Equal("pro", selected.Profile.ProviderPlanType); Assert.Equal("Personnel", selected.Profile.OrganizationName);
            Assert.Equal(30, selected.Snapshot!.Weekly!.RemainingPercent); Assert.Null(selected.Error);
            var retained = service.State.Accounts.Single(a => a.Profile.Id == work);
            Assert.False(retained.IsActive); Assert.Equal(first.Snapshot, retained.Snapshot);
        }
        await using var restarted = new TrackerService(directory.File("missing-auth.json"), options);
        await restarted.InitializeAsync();
        Assert.Equal(new[] { work, personal }, restarted.State.Accounts.Select(a => a.Profile.Id));
        Assert.Equal(personal, restarted.State.ActiveAccount!.Profile.Id);
        ClaudeFixture.SignIn(directory, "work@example.test", organization: Work.ToString(), organizationName: "Fictional company", organizationType: "claude_team");
        await restarted.RefreshAsync();
        Assert.Equal(work, restarted.State.ActiveAccount!.Profile.Id);
        Assert.DoesNotContain("fixture-private", File.ReadAllText(Path.Combine(options.DataDirectory, "settings.json")));
    }
}
