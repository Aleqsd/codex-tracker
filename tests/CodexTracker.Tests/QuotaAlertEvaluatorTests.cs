using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class QuotaAlertEvaluatorTests
{
    private static readonly AccountProfile Profile = new(Guid.Parse("9789eac3-9bfc-4a61-8d8e-30e4cfbe4932"), "demo@example.test");
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BaselineIsQuietAndOneJumpEmitsEveryCrossedThreshold()
    {
        var baseline = QuotaAlertEvaluator.Observe(Profile, Snapshot(0, 22), null);
        Assert.Empty(baseline.Notifications);
        var drop = QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 4), baseline.State);
        Assert.Equal(new int?[] { 20, 10, 5 }, drop.Notifications.Select(n => n.Threshold));
        Assert.All(drop.Notifications, n => Assert.Equal(NotificationKind.Threshold, n.Kind));
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(4, 3), drop.State).Notifications);
    }

    [Fact]
    public void ThresholdsAreIndependentBetweenWeeklyAndShortWindows()
    {
        var previous = QuotaAlertEvaluator.Observe(Profile, Snapshot(0, 50, shortRemaining: 11), null).State;
        var result = QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 19, shortRemaining: 9), previous);
        Assert.Contains(result.Notifications, n => n.Window == UsageWindowKind.Weekly && n.Threshold == 20);
        Assert.Contains(result.Notifications, n => n.Window == UsageWindowKind.Short && n.Threshold == 10);
        Assert.Equal(2, result.Notifications.Count);
    }

    [Fact]
    public void AThresholdDoesNotRepeatAfterQuotaCorrectionWithinTheSameWindow()
    {
        var state = QuotaAlertEvaluator.Observe(Profile, Snapshot(0, 22), null).State;
        state = QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 19), state).State;
        state = QuotaAlertEvaluator.Observe(Profile, Snapshot(4, 25), state).State;
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(6, 18), state).Notifications);
    }

    [Fact]
    public void ResetRequiresBothAForwardWindowAndAnObservedQuotaIncrease()
    {
        var previous = QuotaAlertEvaluator.Observe(Profile, Snapshot(0, 4), null).State;
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 100), previous).Notifications);
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 3, reset: Start.AddDays(8)), previous).Notifications);
        var reset = QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 100, reset: Start.AddDays(8)), previous);
        var notification = Assert.Single(reset.Notifications);
        Assert.Equal(NotificationKind.Reset, notification.Kind);
        Assert.Null(notification.Threshold);
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(4, 100, reset: Start.AddDays(8)), reset.State).Notifications);
        var newPeriod = QuotaAlertEvaluator.Observe(Profile, Snapshot(4, 19, reset: Start.AddDays(8)), reset.State);
        Assert.Equal(20, Assert.Single(newPeriod.Notifications).Threshold);
    }

    [Fact]
    public void StartupSwitchStaleGapsAndOldResponsesDoNotReplayAlerts()
    {
        var previous = QuotaAlertEvaluator.Observe(Profile, Snapshot(0, 25), null).State;
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 4), previous, suppressNotifications: true).Notifications);
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(10, 4), previous).Notifications);
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(0, 4), previous).Notifications);
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 100, reset: Start.AddDays(8)), previous, true).Notifications);
    }

    [Fact]
    public void ARegressingResetTimestampCannotReplayAnAlreadyObservedPeriod()
    {
        var state = QuotaAlertEvaluator.Observe(Profile, Snapshot(0, 4), null).State;
        state = QuotaAlertEvaluator.Observe(Profile, Snapshot(1, 100, reset: Start.AddDays(8)), state).State;
        state = QuotaAlertEvaluator.Observe(Profile, Snapshot(2, 19, reset: Start.AddDays(8)), state).State;
        state = QuotaAlertEvaluator.Observe(Profile, Snapshot(3, 18), state).State;
        var restored = QuotaAlertEvaluator.Observe(Profile, Snapshot(4, 100, reset: Start.AddDays(8)), state);
        Assert.Empty(restored.Notifications);
        Assert.Empty(QuotaAlertEvaluator.Observe(Profile, Snapshot(5, 19, reset: Start.AddDays(8)), restored.State).Notifications);
    }

    private static AccountSnapshot Snapshot(int minutes, double remaining, DateTimeOffset? reset = null, double? shortRemaining = null)
    {
        List<QuotaWindow> windows = [new(100 - remaining, 10080, reset ?? Start.AddDays(7))];
        if (shortRemaining is not null) windows.Add(new(100 - shortRemaining.Value, 300, Start.AddHours(5)));
        return new(Profile.Email, "plus", [new("codex", "Codex", windows)], null, null, Start.AddMinutes(minutes));
    }
}
