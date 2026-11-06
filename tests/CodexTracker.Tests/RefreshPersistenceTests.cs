using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class RefreshPersistenceTests
{
    private static readonly DateTime Sentinel = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-06T10:00:00Z");

    [Fact]
    public void IdenticalSettingsSnapshotsAndTelemetryDoNotRewriteFiles()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var profile = new AccountProfile(Guid.NewGuid(), "fixture@example.test");
        var settings = new StoredSettings([profile], profile.Id, true, [profile.Id]);
        var snapshot = Snapshot(profile.Email, Now);
        var snapshots = new Dictionary<Guid, AccountSnapshot> { [profile.Id] = snapshot };
        var telemetry = new AccountTelemetry([UsageAnalytics.FromSnapshot(profile.Id, snapshot)], new());
        store.SaveSettings(settings);
        store.SaveSnapshots(snapshots);
        store.SaveTelemetry(profile.Id, telemetry);
        var paths = Directory.GetFiles(directory.Root, "*.json*", SearchOption.AllDirectories);
        Assert.Equal(5, paths.Length);
        var contents = paths.ToDictionary(path => path, File.ReadAllBytes);
        foreach (var path in paths) File.SetLastWriteTimeUtc(path, Sentinel);

        store.SaveSettings(settings);
        store.SaveSnapshots(snapshots);
        store.SaveTelemetry(profile.Id, telemetry);

        foreach (var path in paths)
        {
            Assert.Equal(Sentinel, File.GetLastWriteTimeUtc(path));
            Assert.Equal(contents[path], File.ReadAllBytes(path));
        }
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void NewObservationPersistsItsDateAndKeepsThePreviousDistinctSnapshotAsBackup()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var id = Guid.NewGuid();
        var original = Snapshot("fixture@example.test", Now);
        store.SaveSnapshots(new() { [id] = original });
        var originalBytes = File.ReadAllBytes(directory.File("snapshots.json"));
        var next = original with { FetchedAt = Now.AddMinutes(2) };

        store.SaveSnapshots(new() { [id] = next });
        store.SaveSnapshots(new() { [id] = next });

        Assert.Equal(next.FetchedAt, store.LoadSnapshots()[id].FetchedAt);
        Assert.Equal(originalBytes, File.ReadAllBytes(directory.File("snapshots.json.bak")));
        var telemetry = new AccountTelemetry([UsageAnalytics.FromSnapshot(id, next)], new());
        store.SaveTelemetry(id, telemetry);
        var newer = next with { FetchedAt = Now.AddMinutes(4) };
        store.SaveTelemetry(id, new([UsageAnalytics.FromSnapshot(id, newer)], new()));
        Assert.Equal(newer.FetchedAt, Assert.Single(store.LoadTelemetry(id, newer.FetchedAt).Samples).Timestamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalPrimaryStillRepairsMissingOrCorruptBackup(bool corrupt)
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var snapshots = new Dictionary<Guid, AccountSnapshot> { [Guid.NewGuid()] = Snapshot("fixture@example.test", Now) };
        store.SaveSnapshots(snapshots);
        var primary = directory.File("snapshots.json");
        var backup = primary + ".bak";
        var original = File.ReadAllBytes(primary);
        File.SetLastWriteTimeUtc(primary, Sentinel);
        if (corrupt) File.WriteAllText(backup, "broken backup"); else File.Delete(backup);

        store.SaveSnapshots(snapshots);

        Assert.Equal(Sentinel, File.GetLastWriteTimeUtc(primary));
        Assert.Equal(original, File.ReadAllBytes(backup));
        if (corrupt)
            Assert.Equal("broken backup", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "snapshots.json.bak.corrupt-*"))));
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void CorruptPrimaryIsPreservedAndRepairedWithoutReplacingAValidBackup()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var id = Guid.NewGuid();
        var snapshots = new Dictionary<Guid, AccountSnapshot> { [id] = Snapshot("fixture@example.test", Now) };
        store.SaveSnapshots(snapshots);
        var primary = directory.File("snapshots.json");
        var backup = primary + ".bak";
        var original = File.ReadAllBytes(backup);
        File.SetLastWriteTimeUtc(backup, Sentinel);
        File.WriteAllText(primary, "broken primary");
        Assert.Equal(Now, store.LoadSnapshots()[id].FetchedAt);

        store.SaveSnapshots(snapshots);

        Assert.Equal(original, File.ReadAllBytes(primary));
        Assert.Equal(Sentinel, File.GetLastWriteTimeUtc(backup));
        Assert.Equal("broken primary", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "snapshots.json.corrupt-*"))));
    }

    private static AccountSnapshot Snapshot(string email, DateTimeOffset at) => new(email, "pro",
        [new("claude", "Claude", [new(30, 10080, at.AddDays(2)), new(20, 300, at.AddHours(2))])], null, null, at);
}
