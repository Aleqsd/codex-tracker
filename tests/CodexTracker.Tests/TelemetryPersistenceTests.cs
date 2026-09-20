using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class TelemetryPersistenceTests
{
    [Fact]
    public void HistoryAndAlertDeduplicationSurviveRestartAndRemainSeparatedByAccount()
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.UtcNow;
        var first = new AccountProfile(Guid.NewGuid(), "one@example.test");
        var second = new AccountProfile(Guid.NewGuid(), "two@example.test");
        var reset = now.AddDays(4);
        AccountSnapshot Snapshot(double remaining, DateTimeOffset timestamp) =>
            new(first.Email, "plus", [new("codex", null, [new(100 - remaining, 10080, reset)])], null, null, timestamp);
        var state = QuotaAlertEvaluator.Observe(first, Snapshot(22, now.AddMinutes(-2)), null).State;
        var crossed = QuotaAlertEvaluator.Observe(first, Snapshot(19, now), state);
        Assert.Single(crossed.Notifications);
        using (var store = new ProfileStore(directory.Root))
        {
            store.Open();
            store.SaveTelemetry(first.Id, new([new(first.Id, now, 19, reset)], crossed.State));
            store.SaveTelemetry(second.Id, new([new(second.Id, now, 80, reset)], new()));
        }
        using var resumed = new ProfileStore(directory.Root);
        resumed.Open();
        var data = resumed.LoadTelemetry(first.Id, now);
        Assert.Equal(19, Assert.Single(data.Samples).WeeklyRemaining);
        Assert.Equal(80, Assert.Single(resumed.LoadTelemetry(second.Id, now).Samples).WeeklyRemaining);
        var recovered = QuotaAlertEvaluator.Observe(first, Snapshot(25, now.AddMinutes(1)), data.Alerts);
        var repeated = QuotaAlertEvaluator.Observe(first, Snapshot(18, now.AddMinutes(2)), recovered.State);
        Assert.Empty(repeated.Notifications);
        resumed.DeleteTelemetry(first.Id);
        Assert.Empty(resumed.LoadTelemetry(first.Id, now).Samples);
        Assert.Single(resumed.LoadTelemetry(second.Id, now).Samples);
    }

    [Fact]
    public void LoadingPrunesExpiredFutureAndForeignSamplesAndMalformedData()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        store.SaveTelemetry(id, new([
            new(id, now.AddDays(-91), 80, now.AddDays(1)),
            new(id, now.AddDays(1), 70, now.AddDays(1)),
            new(Guid.NewGuid(), now, 60, now.AddDays(1)),
            new(id, now, 50, now.AddDays(1))], new()));
        Assert.Equal(50, Assert.Single(store.LoadTelemetry(id, now).Samples).WeeklyRemaining);
        File.WriteAllText(directory.File(Path.Combine("usage", $"{id:D}.json")), "{broken");
        Assert.Empty(store.LoadTelemetry(id, now).Samples);
    }

    [Fact]
    public void ACacheCopiedToAnotherAccountCannotTransferHistoryOrAlertState()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        store.SaveTelemetry(id, new([new(id, now, 19, now.AddDays(2))],
            new(new(19, now.AddDays(2), now, NotifiedThresholdMask: 1))));
        File.Copy(directory.File(Path.Combine("usage", $"{id:D}.json")), directory.File(Path.Combine("usage", $"{other:D}.json")));
        var loaded = store.LoadTelemetry(other, now);
        Assert.Empty(loaded.Samples);
        Assert.Null(loaded.Alerts.Weekly);
    }
}
