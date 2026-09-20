using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class PowerLifecycleTests
{
    [Fact]
    public async Task SuspensionStopsCollectionAndResumeReadsTheCurrentIdentitySilently()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        await File.WriteAllBytesAsync(auth, TestFixtures.Auth("a@example.test", "a"));
        var reader = new Reader((profile, _) => Task.FromResult(Snapshot(profile.Email, 22)));
        await using var tracker = new TrackerService(auth, Options(directory), reader);
        var notifications = new List<QuotaNotification>();
        tracker.Notification += (_, notification) => notifications.Add(notification);
        await tracker.InitializeAsync();
        var original = Assert.Single(tracker.State.Accounts).Snapshot;
        await tracker.SuspendAsync();
        await tracker.RefreshAsync();
        Assert.Equal(1, reader.Calls);
        Assert.False(tracker.State.IsBusy);
        await File.WriteAllBytesAsync(auth, TestFixtures.Auth("b@example.test", "b"));
        await tracker.ResumeAsync();
        Assert.Equal(2, reader.Calls);
        Assert.Equal("b@example.test", Assert.Single(tracker.State.Accounts, a => a.IsActiveInCodex).Profile.Email);
        Assert.Same(original, Assert.Single(tracker.State.Accounts, a => !a.IsActiveInCodex).Snapshot);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task AResponseStartedBeforeSleepCannotReplaceTheFreshResumeObservation()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        var originalAuth = TestFixtures.Auth("a@example.test", "a");
        await File.WriteAllBytesAsync(auth, originalAuth);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader(async (profile, call) =>
        {
            if (call == 2) { started.TrySetResult(); await release.Task; return Snapshot(profile.Email, 3); }
            return Snapshot(profile.Email, call == 1 ? 22 : 70);
        });
        await using var tracker = new TrackerService(auth, Options(directory), reader);
        var alerts = new List<QuotaNotification>();
        tracker.Notification += (_, notification) => alerts.Add(notification);
        await tracker.InitializeAsync();
        var oldRequest = tracker.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await tracker.SuspendAsync();
            await tracker.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(70, Assert.Single(tracker.State.Accounts).Snapshot!.Weekly!.RemainingPercent);
        }
        finally { release.TrySetResult(); }
        await oldRequest.WaitAsync(TimeSpan.FromSeconds(5));
        var account = Assert.Single(tracker.State.Accounts);
        Assert.Equal(70, account.Snapshot!.Weekly!.RemainingPercent);
        Assert.DoesNotContain(tracker.GetHistory(account.Profile.Id), sample => sample.WeeklyRemaining == 3);
        Assert.Empty(alerts);
        Assert.Equal(originalAuth, await File.ReadAllBytesAsync(auth));
    }

    [Fact]
    public async Task PowerEventsBeforeInitializationDoNotStartRequestsOrPreventStartup()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        await File.WriteAllBytesAsync(auth, TestFixtures.Auth());
        var reader = new Reader((profile, _) => Task.FromResult(Snapshot(profile.Email, 80)));
        await using var tracker = new TrackerService(auth, Options(directory), reader);
        await tracker.SuspendAsync();
        await tracker.ResumeAsync();
        Assert.Equal(0, reader.Calls);
        await tracker.InitializeAsync();
        Assert.Equal(1, reader.Calls);
        Assert.NotNull(Assert.Single(tracker.State.Accounts).Snapshot);
    }

    [Fact]
    public async Task SuspensionDuringAThresholdBatchStopsTheRemainingNotifications()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        await File.WriteAllBytesAsync(auth, TestFixtures.Auth());
        var baseline = Snapshot("demo@example.test", 22);
        var reader = new Reader((_, call) => Task.FromResult(call == 1 ? baseline : baseline with
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Buckets = [new("codex", "Codex", [new(96, 10080, baseline.Weekly!.ResetsAt), baseline.Short!])]
        }));
        await using var tracker = new TrackerService(auth, Options(directory), reader);
        await tracker.InitializeAsync();
        var alerts = new List<QuotaNotification>();
        tracker.Notification += (_, notification) =>
        {
            alerts.Add(notification);
            tracker.SuspendAsync().GetAwaiter().GetResult();
        };
        await tracker.RefreshAsync();
        Assert.Equal(20, Assert.Single(alerts).Threshold);
        await tracker.RefreshAsync();
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    public async Task FirstResumeObservationDoesNotReplayThresholdsCrossedWhileAsleep()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        await File.WriteAllBytesAsync(auth, TestFixtures.Auth());
        var baseline = Snapshot("demo@example.test", 22);
        var reader = new Reader((_, call) => Task.FromResult(call == 1 ? baseline : baseline with
        {
            FetchedAt = DateTimeOffset.UtcNow,
            Buckets = [new("codex", "Codex", [new(96, 10080, baseline.Weekly!.ResetsAt), baseline.Short!])]
        }));
        await using var tracker = new TrackerService(auth, Options(directory), reader);
        var alerts = new List<QuotaNotification>();
        tracker.Notification += (_, notification) => alerts.Add(notification);
        await tracker.InitializeAsync();
        await tracker.SuspendAsync();
        await tracker.ResumeAsync();
        var account = Assert.Single(tracker.State.Accounts);
        Assert.Equal(4, account.Snapshot!.Weekly!.RemainingPercent);
        Assert.Empty(alerts);
        Assert.Null(tracker.GetForecast(account.Profile.Id).EstimatedExhaustionAt);
    }

    private static TrackerServiceOptions Options(TestDirectory directory) => new()
    {
        DataDirectory = directory.File("data"), AutomaticRefresh = false, MonitorAuthChanges = false,
        RequestTimeout = TimeSpan.FromSeconds(5)
    };
    private static AccountSnapshot Snapshot(string email, double remaining) => new(email, "plus",
        [new("codex", "Codex", [new(100 - remaining, 10080, DateTimeOffset.UtcNow.AddDays(2)), new(15, 300, DateTimeOffset.UtcNow.AddHours(2))])],
        null, null, DateTimeOffset.UtcNow);
    private sealed class Reader(Func<AccountProfile, int, Task<AccountSnapshot>> read) : IAccountUsageReader
    {
        private int _calls;
        public int Calls => _calls;
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken)
            => read(profile, Interlocked.Increment(ref _calls));
    }
}
