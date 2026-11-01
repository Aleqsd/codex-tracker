using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class SmartReminderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");
    private static readonly Guid AccountId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }

    private static TrackerState State(double weeklyLeft) => new([
        new(new(AccountId, "work@example.test"), new("work@example.test", "plus",
            [new("codex", null, [new(100 - weeklyLeft, 10080, Now.AddMinutes(50)), new(0, 300, Now.AddHours(4))])], 0, [], Now.AddMinutes(-2)), IsActiveInCodex: true)], AccountId);

    [Theory]
    [InlineData(null, true)]
    [InlineData(10.0, true)]
    [InlineData(50.0, true)]
    [InlineData(50.5, false)]
    [InlineData(90.0, false)]
    public void ARefillMattersOnlyForAWindowAtLeastHalfUsed(double? remaining, bool worth) => Assert.Equal(worth, SmartReminders.Worth(remaining));

    [Theory]
    [InlineData(20, 1)]
    [InlineData(80, 0)]
    public async Task ARuleReminderBeforeTheResetIsSkippedWhileTheWeekIsMostlyLeft(double weeklyLeft, int expected)
    {
        using var directory = new TestDirectory();
        var shown = new List<ReminderOccurrence>();
        ReminderRule[] rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Windows])];
        var dispatcher = new ReminderDispatcher(new ReminderJournal(directory.Root), new NotificationSecretStore(directory.Root), new(new HttpClient()),
            () => State(weeklyLeft), () => rules, () => new(), () => new Dictionary<string, DateTimeOffset>(),
            rows => { shown.AddRange(rows); return new(DeliveryStatus.Shown, "ok"); }, new Clock { Now = Now }, smartReminders: () => true);
        await dispatcher.TickAsync();
        Assert.Equal(expected, shown.Count);
    }

    [Fact]
    public void AQuotaRefilledNoticeNeedsAWindowThatWasUsed()
    {
        var used = new QuotaNotification(AccountId, "work@example.test", NotificationKind.Reset, null, Now, PreviousRemaining: 12);
        var barely = used with { PreviousRemaining = 85 };
        var preferences = new CodexTracker.App.TrackerPreferences { ResetNotifications = true, SmartReminders = true };
        Assert.True(CodexTracker.App.NotificationPolicy.IsEnabled(used, preferences));
        Assert.False(CodexTracker.App.NotificationPolicy.IsEnabled(barely, preferences));
        Assert.True(CodexTracker.App.NotificationPolicy.IsEnabled(barely, preferences with { SmartReminders = false }));
    }
}
