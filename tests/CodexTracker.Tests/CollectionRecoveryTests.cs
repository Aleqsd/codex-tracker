using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class CollectionRecoveryTests
{
    [Theory]
    [InlineData(false, "settings.json")] [InlineData(true, "settings.json")]
    [InlineData(false, "snapshots.json")] [InlineData(true, "snapshots.json")]
    [InlineData(false, "usage")] [InlineData(true, "usage")]
    public async Task PersistenceFailureAlwaysFinishesAndIdenticalRetryPersistsPendingTelemetry(bool claude, string blocked)
    {
        using var directory = new TestDirectory();
        PrepareIdentity(directory, claude);
        var now = DateTimeOffset.UtcNow;
        var reader = new Reader(Snapshot(claude, now.AddMinutes(-4), 50));
        await using var service = Service(directory, claude, reader);
        await service.InitializeAsync();
        var id = service.State.ActiveAccount!.Profile.Id;
        var notices = new List<QuotaNotification>();
        service.Notification += (_, notice) => notices.Add(notice);
        reader.Value = Snapshot(claude, now.AddMinutes(-2), 4);
        var path = blocked == "usage" ? directory.File($"data/usage/{id:D}.json") : directory.File($"data/{blocked}");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = (await service.RefreshWithResultAsync()).Single(s => s.Provider == Provider(claude));
            Assert.Equal(RefreshOutcome.Failed, result.Outcome);
            Assert.Equal("persistence_failed", result.ErrorCode);
            Assert.Equal(1, result.ConsecutiveFailures);
            Assert.NotNull(result.CompletedAt);
            Assert.Equal(reader.Value.FetchedAt, result.ObservedAt);
            Assert.False(service.State.IsBusy);
            Assert.False(service.State.ActiveAccount!.IsRefreshing);
            Assert.Same(reader.Value, service.State.ActiveAccount.Snapshot);
            if (blocked == "usage") Assert.Empty(notices);
            else Assert.Equal(new int?[] { 20, 10, 5 }, notices.Select(n => n.Threshold));
        }
        var recovered = (await service.RefreshWithResultAsync()).Single(s => s.Provider == Provider(claude));
        Assert.Equal(RefreshOutcome.Unchanged, recovered.Outcome);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.ErrorCode);
        using var store = new ProfileStore(directory.File("data"));
        Assert.Equal(reader.Value.FetchedAt, store.LoadSnapshots()[id].FetchedAt);
        Assert.Equal(reader.Value.FetchedAt, store.LoadTelemetry(id, now).Samples[^1].Timestamp);
        if (blocked == "usage") Assert.Empty(notices);
        else Assert.Equal(new int?[] { 20, 10, 5 }, notices.Select(n => n.Threshold)); // Already durably deduplicated; never replay.
    }

    [Fact]
    public async Task RedetectedMetadataSaveFailureStillEndsTheSameIdentityCollection()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory, organizationName: "Before");
        var reader = new Reader(Snapshot(true, DateTimeOffset.UtcNow.AddMinutes(-2)));
        await using var service = Service(directory, true, reader);
        await service.InitializeAsync();
        FileStream? blocked = null;
        reader.BeforeRead = () =>
        {
            ClaudeFixture.SignIn(directory, organizationName: "After");
            blocked = new FileStream(directory.File("data/settings.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        };
        reader.Value = reader.Value with { FetchedAt = DateTimeOffset.UtcNow };
        try
        {
            var result = (await service.RefreshWithResultAsync()).Single(s => s.Provider == AccountProvider.ClaudeCode);
            Assert.Equal(RefreshOutcome.Failed, result.Outcome);
            Assert.Equal("persistence_failed", result.ErrorCode);
            Assert.Equal("After", service.State.ActiveAccount!.Profile.OrganizationName);
            Assert.False(service.State.IsBusy);
            Assert.False(service.State.ActiveAccount.IsRefreshing);
            Assert.Same(reader.Value, service.State.ActiveAccount.Snapshot);
        }
        finally { blocked?.Dispose(); reader.BeforeRead = null; }
        await service.RefreshAsync();
        Assert.Null(service.State.ActiveAccount!.Error);
    }

    [Theory]
    [InlineData(false, true)] [InlineData(true, true)]
    [InlineData(false, false)] [InlineData(true, false)]
    public async Task CorrectedClockRecoversSnapshotOrTelemetryPreservesOriginalsAndKeepsDeduplication(bool claude, bool futureSnapshot)
    {
        using var directory = new TestDirectory();
        PrepareIdentity(directory, claude);
        var now = DateTimeOffset.UtcNow;
        var profile = await Profile(directory, claude);
        var previous = Snapshot(claude, futureSnapshot ? now.AddDays(2) : now.AddMinutes(-5), 4);
        var future = Snapshot(claude, now.AddDays(2), 4);
        Seed(directory, profile, previous, new([UsageAnalytics.FromSnapshot(profile.Id, future)],
            new(new(4, future.Weekly!.ResetsAt, future.FetchedAt, 7, future.Weekly.ResetsAt, future.Weekly.ResetsAt))));
        var originalUsage = File.ReadAllBytes(directory.File($"data/usage/{profile.Id:D}.json"));
        var reader = new Reader(Snapshot(claude, now.AddMinutes(-2), 25));
        var notices = new List<QuotaNotification>();
        await using (var service = Service(directory, claude, reader))
        {
            service.Notification += (_, notice) => notices.Add(notice);
            await service.InitializeAsync();
            Assert.Equal(reader.Value.FetchedAt, service.State.ActiveAccount!.Snapshot!.FetchedAt);
            Assert.Equal(RefreshOutcome.Updated, service.GetRefreshStatus().Single(s => s.Provider == Provider(claude)).Outcome);
            Assert.All(service.GetHistory(profile.Id), sample => Assert.True(sample.Timestamp <= now));
            Assert.Equal(originalUsage, File.ReadAllBytes(directory.File($"data/clock-recovery/{profile.Id:D}.usage.json")));
            using var store = new ProfileStore(directory.File("data"));
            var alerts = store.LoadTelemetry(profile.Id, now).Alerts.Weekly!;
            Assert.Equal(reader.Value.FetchedAt, alerts.ObservedAt);
            Assert.Equal(7, alerts.NotifiedThresholdMask);
            Assert.Equal(reader.Value.Weekly!.ResetsAt, alerts.ForecastNotifiedFor);
            reader.Value = Snapshot(claude, now.AddMinutes(-1), 18);
            await service.RefreshAsync();
            Assert.Equal(reader.Value.FetchedAt, service.GetHistory(profile.Id)[^1].Timestamp);
            Assert.Empty(notices);
        }
        var archive = Directory.GetFiles(directory.File("data/clock-recovery")).ToDictionary(p => p, File.ReadAllBytes);
        reader.Value = Snapshot(claude, now, 3);
        await using (var restarted = Service(directory, claude, reader))
        {
            restarted.Notification += (_, notice) => notices.Add(notice);
            await restarted.InitializeAsync();
            Assert.Equal(now, restarted.State.ActiveAccount!.Snapshot!.FetchedAt);
            Assert.Equal(now, restarted.GetHistory(profile.Id)[^1].Timestamp);
            Assert.Empty(notices);
        }
        Assert.Equal(2, Directory.GetFiles(directory.File("data/clock-recovery")).Length);
        foreach (var (path, content) in archive) Assert.Equal(content, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NormalOlderAndFutureIncomingObservationsRemainRejected(bool claude)
    {
        using var directory = new TestDirectory();
        PrepareIdentity(directory, claude);
        var now = DateTimeOffset.UtcNow;
        var reader = new Reader(Snapshot(claude, now.AddMinutes(-2)));
        await using var service = Service(directory, claude, reader);
        await service.InitializeAsync();
        var original = service.State.ActiveAccount!.Snapshot;
        foreach (var at in new[] { now.AddMinutes(-3), now.AddDays(2) })
        {
            reader.Value = Snapshot(claude, at, 1);
            var result = (await service.RefreshWithResultAsync()).Single(s => s.Provider == Provider(claude));
            Assert.Equal(RefreshOutcome.Failed, result.Outcome);
            Assert.Same(original, service.State.ActiveAccount.Snapshot);
            Assert.False(service.State.IsBusy);
        }
        Assert.False(Directory.Exists(directory.File("data/clock-recovery")));
    }

    [Fact]
    public async Task ArchiveFailureRetainsFutureOriginalAndCanRetryAfterStorageRecovery()
    {
        using var directory = new TestDirectory();
        PrepareIdentity(directory, false);
        var now = DateTimeOffset.UtcNow;
        var profile = await Profile(directory, false);
        var future = Snapshot(false, now.AddDays(1));
        Seed(directory, profile, future, new([UsageAnalytics.FromSnapshot(profile.Id, future)], new()));
        var archivePath = directory.File("data/clock-recovery");
        File.WriteAllText(archivePath, "fixture obstructing directory creation");
        var reader = new Reader(Snapshot(false, now));
        await using var service = Service(directory, false, reader);
        await service.InitializeAsync();
        Assert.Equal(future.FetchedAt, service.State.ActiveAccount!.Snapshot!.FetchedAt);
        Assert.Equal("persistence_failed", Assert.Single(service.GetRefreshStatus()).ErrorCode);
        Assert.False(service.State.IsBusy);
        File.Delete(archivePath);
        await service.RefreshAsync();
        Assert.Equal(now, service.State.ActiveAccount.Snapshot.FetchedAt);
        Assert.Equal(RefreshOutcome.Updated, Assert.Single(service.GetRefreshStatus()).Outcome);
    }

    [Fact]
    public async Task RemovingAnInactiveAccountDeletesItsClockArchivesAndKeepsOtherAccounts()
    {
        using var directory = new TestDirectory();
        PrepareIdentity(directory, false);
        var now = DateTimeOffset.UtcNow;
        var active = await Profile(directory, false);
        var inactive = new AccountProfile(Guid.NewGuid(), "other@example.test");
        using (var store = new ProfileStore(directory.File("data")))
        {
            store.Open();
            store.SaveSettings(new([active, inactive], active.Id, true, [active.Id, inactive.Id]));
            var snapshots = new[] { active, inactive }.ToDictionary(p => p.Id, p => Snapshot(false, now.AddDays(1)) with { Email = p.Email });
            store.SaveSnapshots(snapshots);
            foreach (var profile in new[] { active, inactive })
            {
                store.SaveTelemetry(profile.Id, new([UsageAnalytics.FromSnapshot(profile.Id, snapshots[profile.Id])], new()));
                store.PreserveClockRecovery(profile.Id, snapshots[profile.Id]);
            }
        }
        var archive = directory.File("data/clock-recovery");
        var activeOriginals = Directory.GetFiles(archive, $"{active.Id:D}.*").ToDictionary(p => p, File.ReadAllBytes);
        await using var service = Service(directory, false, new(Snapshot(false, now)));
        await service.InitializeAsync();
        await service.RemoveAccountAsync(inactive.Id);
        Assert.DoesNotContain(service.State.Accounts, account => account.Profile.Id == inactive.Id);
        Assert.Empty(Directory.GetFiles(archive, $"{inactive.Id:D}.*"));
        Assert.False(File.Exists(directory.File($"data/usage/{inactive.Id:D}.json")));
        Assert.Equal(2, Directory.GetFiles(archive).Length);
        foreach (var (path, content) in activeOriginals) Assert.Equal(content, File.ReadAllBytes(path));
    }

    [Fact]
    public void RemovingTelemetryForgetsTheArchiveCacheSoTheIdentityCanBeImportedAgain()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var id = Guid.NewGuid();
        var snapshot = Snapshot(false, DateTimeOffset.UtcNow.AddDays(1));
        var telemetry = new AccountTelemetry([UsageAnalytics.FromSnapshot(id, snapshot)], new());
        store.SaveTelemetry(id, telemetry);
        store.PreserveClockRecovery(id, snapshot);
        store.DeleteTelemetry(id);
        store.SaveTelemetry(id, telemetry);
        store.PreserveClockRecovery(id, snapshot);
        Assert.Equal(2, Directory.GetFiles(directory.File("clock-recovery")).Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PruningAnInactiveAccountPreservesFutureOriginalsBeforeActivation(bool archiveBlocked)
    {
        using var directory = new TestDirectory();
        PrepareIdentity(directory, false);
        var now = DateTimeOffset.UtcNow;
        var active = await Profile(directory, false);
        var inactive = new AccountProfile(Guid.NewGuid(), "other@example.test");
        var current = Snapshot(false, now);
        var future = Snapshot(false, now.AddDays(2)) with { Email = inactive.Email };
        var old = future with { FetchedAt = now - UsageAnalytics.Retention + TimeSpan.FromHours(1) };
        using (var store = new ProfileStore(directory.File("data")))
        {
            store.Open();
            store.SaveSettings(new([active, inactive], active.Id, true, [active.Id, inactive.Id]));
            store.SaveSnapshots(new() { [active.Id] = current, [inactive.Id] = future });
            store.SaveTelemetry(inactive.Id, new([UsageAnalytics.FromSnapshot(inactive.Id, old), UsageAnalytics.FromSnapshot(inactive.Id, future)], new()));
        }
        var usagePath = directory.File($"data/usage/{inactive.Id:D}.json");
        var original = File.ReadAllBytes(usagePath);
        var reader = new Reader(current);
        await using var service = Service(directory, false, reader);
        await service.InitializeAsync();
        Assert.Equal(old.FetchedAt, Assert.Single(service.GetHistory(inactive.Id)).Timestamp);
        var prune = typeof(TrackerService).GetMethod("PruneOldHistory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var archive = directory.File("data/clock-recovery");
        if (archiveBlocked)
        {
            File.WriteAllText(archive, "fixture obstructing directory creation");
            prune.Invoke(service, [now.AddHours(2)]);
            Assert.Equal(original, File.ReadAllBytes(usagePath));
            Assert.Equal(old.FetchedAt, Assert.Single(service.GetHistory(inactive.Id)).Timestamp);
            File.Delete(archive);
        }
        prune.Invoke(service, [now.AddHours(2)]);
        Assert.Empty(service.GetHistory(inactive.Id));
        var archivedUsage = Path.Combine(archive, $"{inactive.Id:D}.usage.json");
        Assert.Equal(original, File.ReadAllBytes(archivedUsage));
        File.WriteAllBytes(directory.File("auth.json"), TestFixtures.Auth(inactive.Email, "other"));
        reader.Value = current with { Email = inactive.Email };
        await service.RefreshAsync();
        Assert.Equal(inactive.Id, service.State.ActiveAccount!.Profile.Id);
        Assert.Equal(now, service.State.ActiveAccount.Snapshot!.FetchedAt);
        Assert.Equal(now, Assert.Single(service.GetHistory(inactive.Id)).Timestamp);
        Assert.Equal(original, File.ReadAllBytes(archivedUsage));
    }

    private static void PrepareIdentity(TestDirectory directory, bool claude)
    {
        if (claude) ClaudeFixture.SignIn(directory);
        else File.WriteAllBytes(directory.File("auth.json"), TestFixtures.Auth());
    }

    private static async Task<AccountProfile> Profile(TestDirectory directory, bool claude) => new(Guid.NewGuid(), "demo@example.test", Provider(claude),
        claude ? (await ClaudeCodeIdentity.ReadAsync(ClaudeFixture.Location(directory), default)).AccountId : null);

    private static void Seed(TestDirectory directory, AccountProfile profile, AccountSnapshot snapshot, AccountTelemetry telemetry)
    {
        using var store = new ProfileStore(directory.File("data"));
        store.Open();
        store.SaveSettings(new([profile], profile.Id, true, [profile.Id]));
        store.SaveSnapshots(new() { [profile.Id] = snapshot });
        store.SaveTelemetry(profile.Id, telemetry);
    }

    private static AccountProvider Provider(bool claude) => claude ? AccountProvider.ClaudeCode : AccountProvider.Codex;
    private static TrackerService Service(TestDirectory directory, bool claude, Reader reader) => new(directory.File("auth.json"),
        new() { DataDirectory = directory.File("data"), DetectClaudeCode = claude, ClaudeLocation = ClaudeFixture.Location(directory),
            ClaudeDesktopUsagePaths = [], AutomaticRefresh = false, MonitorAuthChanges = false }, claude ? null : reader, claude ? reader : null);

    private static readonly DateTimeOffset Reset = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static AccountSnapshot Snapshot(bool claude, DateTimeOffset at, double remaining = 50) => new("demo@example.test", "pro",
        [new(claude ? "claude" : "codex", null, [new(100 - remaining, 10080, Reset)])], null, null, at);

    private sealed class Reader(AccountSnapshot value) : IAccountUsageReader
    {
        public AccountSnapshot Value = value;
        public Action? BeforeRead;
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken)
        { BeforeRead?.Invoke(); return Task.FromResult(Value); }
    }
}
