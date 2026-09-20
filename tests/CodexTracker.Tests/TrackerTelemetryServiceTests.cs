using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class TrackerTelemetryServiceTests
{
    [Fact]
    public async Task ServicePersistsHistoryAndDeduplicationWhileStartupAndSwitchRemainQuiet()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var originalAuth = TestFixtures.Auth("a@example.test", "a");
        await File.WriteAllBytesAsync(authPath, originalAuth);
        var now = DateTimeOffset.UtcNow;
        var reader = new ControlledReader(now.AddMinutes(-4), 22, now.AddDays(7));
        var options = new TrackerServiceOptions { DataDirectory = directory.File("data"), AutomaticRefresh = false, MonitorAuthChanges = false };
        Guid accountId;
        await using (var service = new TrackerService(authPath, options, reader))
        {
            var notifications = new List<QuotaNotification>();
            service.Notification += (_, notification) => notifications.Add(notification);
            await service.InitializeAsync();
            accountId = Assert.Single(service.State.Accounts).Profile.Id;
            Assert.Single(service.GetHistory(accountId));
            Assert.Empty(notifications);
            reader.Timestamp = now.AddMinutes(-2);
            reader.Remaining = 4;
            await service.RefreshAsync();
            Assert.Equal(new int?[] { 20, 10, 5 }, notifications.Select(n => n.Threshold));
            Assert.Equal(2, service.GetHistory(accountId).Count);
            Assert.Equal(originalAuth, await File.ReadAllBytesAsync(authPath));
        }

        reader.Timestamp = now.AddMinutes(-1);
        reader.Remaining = 25;
        await using (var resumed = new TrackerService(authPath, options, reader))
        {
            var notifications = new List<QuotaNotification>();
            resumed.Notification += (_, notification) => notifications.Add(notification);
            await resumed.InitializeAsync();
            Assert.True(resumed.GetHistory(accountId).Count >= 2);
            Assert.Empty(notifications);
            reader.Timestamp = now;
            reader.Remaining = 18;
            await resumed.RefreshAsync();
            Assert.Empty(notifications); // The 20% alert was already emitted before restart.

            await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("b@example.test", "b"));
            reader.Timestamp = now.AddSeconds(10);
            reader.Remaining = 1;
            await resumed.RefreshAsync();
            Assert.Empty(notifications);
            Assert.Null(resumed.GetForecast(accountId).EstimatedExhaustionAt);
            Assert.Contains("Ouvrez ce compte", resumed.GetForecast(accountId).Explanation);
            Assert.True(resumed.GetHistory(accountId).Count >= 2);
        }
    }

    [Fact]
    public async Task ConfirmedResetEmitsOnceAndOldResponsesCannotAlterHistoryOrNotify()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth());
        var now = DateTimeOffset.UtcNow;
        var reader = new ControlledReader(now.AddMinutes(-4), 4, now.AddDays(1));
        await using var service = new TrackerService(authPath,
            new() { DataDirectory = directory.File("data"), AutomaticRefresh = false, MonitorAuthChanges = false }, reader);
        var notifications = new List<QuotaNotification>();
        service.Notification += (_, notification) => notifications.Add(notification);
        await service.InitializeAsync();
        var id = Assert.Single(service.State.Accounts).Profile.Id;
        reader.Timestamp = now.AddMinutes(-2);
        reader.Remaining = 100;
        reader.Reset = now.AddDays(8);
        await service.RefreshAsync();
        Assert.Equal(NotificationKind.Reset, Assert.Single(notifications).Kind);
        var history = service.GetHistory(id);
        var snapshot = service.State.SelectedAccount!.Snapshot;

        reader.Timestamp = now.AddMinutes(-3);
        reader.Remaining = 1;
        await service.RefreshAsync();
        Assert.Same(history, service.GetHistory(id));
        Assert.Same(snapshot, service.State.SelectedAccount!.Snapshot);
        Assert.Single(notifications);
        Assert.NotNull(service.State.SelectedAccount.Error);
    }

    private sealed class ControlledReader(DateTimeOffset timestamp, double remaining, DateTimeOffset reset) : IAccountUsageReader
    {
        public DateTimeOffset Timestamp { get; set; } = timestamp;
        public double Remaining { get; set; } = remaining;
        public DateTimeOffset Reset { get; set; } = reset;
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountSnapshot(profile.Email, "plus", [new("codex", null, [new(100 - Remaining, 10080, Reset)])],
                null, null, Timestamp));
    }
}
