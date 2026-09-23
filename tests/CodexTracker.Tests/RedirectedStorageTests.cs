using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class RedirectedStorageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static AccountSnapshot Snapshot(AccountProfile p, DateTimeOffset at) => new(p.Email, "plus", [], null, null, at);
    private static AccountProfile Profile(int index) => new(Guid.NewGuid(), $"person{index}@example.test");
    private static void Seed(string directory, params AccountProfile[] profiles)
    {
        using var store = new ProfileStore(directory); store.Open();
        store.SaveSettings(new(profiles.ToList(), null, true, profiles.Select(p => p.Id).ToList()));
        store.SaveSnapshots(profiles.ToDictionary(p => p.Id, p => Snapshot(p, Now.AddDays(-1))));
    }

    [Fact]
    public void FiveAndTwoProfilesMergeByEmailKeepingNewestObservationsAndSources()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory();
        var profiles = Enumerable.Range(1, 5).Select(Profile).ToArray();
        Seed(old.Root, profiles);
        var active = profiles[0] with { Id = Guid.NewGuid() }; Seed(live.Root, active, profiles[1]);
        var sourceBytes = File.ReadAllBytes(old.File("settings.json"));
        File.WriteAllText(live.File("notification-secrets.dpapi"), "opaque-protected-fixture");
        File.WriteAllText(live.File("reminder-journal.json"), "existing-send-history");
        using var store = new ProfileStore(live.Root); store.Open();
        var fresh = Snapshot(active, Now); store.SaveSnapshots(new() { [active.Id] = fresh });
        store.RecoverRedirectedStores([old.Root]);
        var result = store.LoadSettings();
        Assert.Equal(5, result.Accounts.Count); Assert.Contains(active, result.Accounts);
        Assert.Equal(5, store.LoadSnapshots().Count); Assert.Equal(fresh.FetchedAt, store.LoadSnapshots()[active.Id].FetchedAt);
        Assert.Equal(Now.AddDays(-1), store.LoadSnapshots()[profiles[4].Id].FetchedAt);
        Assert.Null(store.LoadSnapshots()[profiles[4].Id].AvailableResetCredits);
        Assert.Equal(sourceBytes, File.ReadAllBytes(old.File("settings.json")));
        Assert.Equal("opaque-protected-fixture", File.ReadAllText(live.File("notification-secrets.dpapi")));
        Assert.Equal("existing-send-history", File.ReadAllText(live.File("reminder-journal.json")));
        Assert.Single(Directory.GetDirectories(live.File("recovery")));
    }

    [Fact]
    public void HistoryIsMergedAndRemappedWithoutCrossAccountSamples()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory(); var source = Profile(1); var target = source with { Id = Guid.NewGuid() };
        Seed(old.Root, source); Seed(live.Root, target);
        using (var previous = new ProfileStore(old.Root))
        {
            previous.Open(); previous.SaveTelemetry(source.Id, new([
                new(source.Id, Now.AddHours(-2), 60, Now.AddDays(2)),
                new(Guid.NewGuid(), Now.AddHours(-1), 50, Now.AddDays(2))], new(), source.Id));
            previous.SaveSnapshots(new() { [source.Id] = Snapshot(source, Now) });
        }
        using var store = new ProfileStore(live.Root); store.Open();
        store.SaveTelemetry(target.Id, new([new(target.Id, Now, 40, Now.AddDays(2))], new(), target.Id));
        store.RecoverRedirectedStores([old.Root]);
        var history = store.LoadTelemetry(target.Id, Now);
        Assert.Equal(2, history.Samples.Count); Assert.All(history.Samples, s => Assert.Equal(target.Id, s.AccountId));
        Assert.Equal(Now, store.LoadSnapshots()[target.Id].FetchedAt);
    }

    [Fact]
    public void CompletedMigrationDoesNotResurrectAnIntentionallyRemovedAccount()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory();
        Seed(old.Root, Profile(1));
        using (var store = new ProfileStore(live.Root))
        {
            store.Open(); store.RecoverRedirectedStores([old.Root]);
            store.SaveSettings(new([], null, true)); store.SaveSnapshots([]);
        }
        using var restarted = new ProfileStore(live.Root); restarted.Open(); restarted.RecoverRedirectedStores([old.Root]);
        Assert.Empty(restarted.LoadSettings().Accounts); Assert.Empty(restarted.LoadSnapshots());
    }

    [Fact]
    public void InterruptedMigrationCanReplayWithoutDuplicatingProfiles()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory(); Seed(old.Root, Profile(1), Profile(2));
        using var store = new ProfileStore(live.Root); store.Open(); store.RecoverRedirectedStores([old.Root]);
        var ids = store.LoadSettings().Accounts.Select(p => p.Id).ToArray();
        File.Delete(live.File("storage-migrations.json")); File.Delete(live.File("storage-migrations.json.bak"));
        store.RecoverRedirectedStores([old.Root]); Assert.Equal(ids, store.LoadSettings().Accounts.Select(p => p.Id));
    }

    [Fact]
    public void SameGuidForDifferentEmailsNeverOverwritesTheOtherAccount()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory(); var one = Profile(1); var two = Profile(2) with { Id = one.Id };
        Seed(old.Root, two); Seed(live.Root, one);
        using var store = new ProfileStore(live.Root); store.Open(); store.RecoverRedirectedStores([old.Root]);
        Assert.Equal(2, store.LoadSettings().Accounts.Count);
        foreach (var p in store.LoadSettings().Accounts) Assert.Equal(p.Email, store.LoadSnapshots()[p.Id].Email);
    }

    [Fact]
    public void DamagedSourceDoesNotResetDestinationOrRecordSuccess()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory(); Seed(old.Root, Profile(1)); Seed(live.Root, Profile(2));
        File.WriteAllText(old.File("settings.json"), "{bad"); File.WriteAllText(old.File("settings.json.bak"), "{bad");
        var before = File.ReadAllBytes(live.File("settings.json"));
        using var store = new ProfileStore(live.Root); store.Open();
        Assert.Throws<TrackerException>(() => store.RecoverRedirectedStores([old.Root]));
        Assert.Equal(before, File.ReadAllBytes(live.File("settings.json"))); Assert.False(File.Exists(live.File("storage-migrations.json")));
    }

    [Fact]
    public void RunningSourceCannotBeMergedConcurrently()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory(); Seed(old.Root, Profile(1));
        using var running = new ProfileStore(old.Root); running.Open();
        using var store = new ProfileStore(live.Root); store.Open();
        Assert.Throws<IOException>(() => store.RecoverRedirectedStores([old.Root]));
        Assert.False(File.Exists(live.File("settings.json")));
    }

    [Fact]
    public void CorruptedLedgerNeverReplaysPreviouslyCompletedMigrations()
    {
        using var old = new TestDirectory(); using var live = new TestDirectory(); Seed(old.Root, Profile(1));
        using var store = new ProfileStore(live.Root); store.Open(); store.RecoverRedirectedStores([old.Root]);
        store.SaveSettings(new([], null, true)); File.WriteAllText(live.File("storage-migrations.json"), "broken");
        Assert.Throws<TrackerException>(() => store.RecoverRedirectedStores([old.Root])); Assert.Empty(store.LoadSettings().Accounts);
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("C:\\a b\\", "\"C:\\a b\\\\\"")]
    public void RelaunchArgumentsPreserveSpacesQuotesAndTrailingSlashes(string value, string expected) =>
        Assert.Equal(expected, DesktopEnvironment.QuoteArgument(value));

    [Fact]
    public void PhysicalStoreProbeCleansUpItsFile()
    {
        using var directory = new TestDirectory();
        DesktopEnvironment.IsStorageRedirected(directory.Root);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }
}
