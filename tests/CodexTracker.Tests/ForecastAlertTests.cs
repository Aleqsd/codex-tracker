using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ForecastAlertTests
{
    private static readonly AccountProfile Profile = new(Guid.Parse("7f0e1d2c-3b4a-4596-8a7b-6c5d4e3f2a10"), "pace@example.test");
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeeklyReset = Start.AddDays(3);
    private static readonly DateTimeOffset ShortReset = Start.AddHours(4);

    [Fact]
    public void WarnsOnceWhenThePaceEmptiesAWindowWithinTheLead()
    {
        var state = Baseline();
        var (next, notification) = ForecastAlert.Observe(Profile, Snapshot(), state, Forecast(TimeSpan.FromMinutes(45), UsageWindowKind.Short));
        Assert.NotNull(notification);
        Assert.Equal(NotificationKind.Forecast, notification.Kind);
        Assert.Equal(UsageWindowKind.Short, notification.Window);
        Assert.Equal(Start.AddMinutes(45), notification.ExhaustionAt);
        Assert.Equal(ShortReset, next.Short!.ForecastNotifiedFor);
        Assert.Null(next.Weekly!.ForecastNotifiedFor);
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), next, Forecast(TimeSpan.FromMinutes(30), UsageWindowKind.Short)).Notification);
    }

    [Fact]
    public void StaysQuietOutsideTheLeadAfterTheResetOrWithoutAnEstimate()
    {
        var state = Baseline();
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), state, Forecast(ForecastAlert.Lead + TimeSpan.FromMinutes(1), UsageWindowKind.Short)).Notification);
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), state, Forecast(TimeSpan.Zero, UsageWindowKind.Short)).Notification);
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), state, new(null, null, "Rythme irrégulier.", UsageWindowKind.Short)).Notification);
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), state, new(null, null, "Le reset précède.", UsageWindowKind.Weekly, LastsUntilReset: true)).Notification);
        var afterReset = new UsageForecast(TimeSpan.FromMinutes(30), ShortReset.AddMinutes(1), "Après le reset.", UsageWindowKind.Short);
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), state, afterReset).Notification);
        Assert.Equal(state, ForecastAlert.Observe(Profile, Snapshot(), state, afterReset).State);
    }

    [Fact]
    public void RequiresTheSameDatedPeriodAsTheAlertState()
    {
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), new(), Forecast(TimeSpan.FromMinutes(20), UsageWindowKind.Weekly)).Notification);
        var undated = Snapshot(undated: true);
        var state = QuotaAlertEvaluator.Observe(Profile, undated, null).State;
        Assert.Null(ForecastAlert.Observe(Profile, undated, state, Forecast(TimeSpan.FromMinutes(20), UsageWindowKind.Weekly)).Notification);
        var moved = Snapshot(weeklyReset: WeeklyReset.AddDays(7));
        Assert.Null(ForecastAlert.Observe(Profile, moved, Baseline(), Forecast(TimeSpan.FromMinutes(20), UsageWindowKind.Weekly)).Notification);
    }

    [Fact]
    public void ANewPeriodRearmsAndTheMarkerSurvivesThresholdEvaluation()
    {
        var (warned, _) = ForecastAlert.Observe(Profile, Snapshot(), Baseline(), Forecast(TimeSpan.FromMinutes(40), UsageWindowKind.Weekly));
        var kept = QuotaAlertEvaluator.Observe(Profile, Snapshot(minutes: 2, weekly: 17), warned).State;
        Assert.Equal(WeeklyReset, kept.Weekly!.ForecastNotifiedFor);
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(minutes: 2, weekly: 17), kept, Forecast(TimeSpan.FromMinutes(20), UsageWindowKind.Weekly)).Notification);
        var nextPeriod = WeeklyReset.AddDays(7);
        var renewed = QuotaAlertEvaluator.Observe(Profile, Snapshot(minutes: 4, weekly: 90, weeklyReset: nextPeriod), kept).State;
        var forecast = new UsageForecast(TimeSpan.FromMinutes(20), Start.AddMinutes(24), "Rythme soutenu.", UsageWindowKind.Weekly);
        Assert.NotNull(ForecastAlert.Observe(Profile, Snapshot(minutes: 4, weekly: 90, weeklyReset: nextPeriod), renewed, forecast).Notification);
    }

    [Fact]
    public void SuppressedObservationsRecordTheWarningWithoutNotifying()
    {
        var (state, notification) = ForecastAlert.Observe(Profile, Snapshot(), Baseline(), Forecast(TimeSpan.FromMinutes(10), UsageWindowKind.Short), suppressNotifications: true);
        Assert.Null(notification);
        Assert.Equal(ShortReset, state.Short!.ForecastNotifiedFor);
        Assert.Null(ForecastAlert.Observe(Profile, Snapshot(), state, Forecast(TimeSpan.FromMinutes(5), UsageWindowKind.Short)).Notification);
    }

    private static QuotaAlertState Baseline() => QuotaAlertEvaluator.Observe(Profile, Snapshot(), null).State;
    private static UsageForecast Forecast(TimeSpan left, UsageWindowKind window) =>
        new(left, Start + left, "Si le rythme observé reste comparable.", window);
    private static AccountSnapshot Snapshot(int minutes = 0, double weekly = 40, double shortRemaining = 30, DateTimeOffset? weeklyReset = null, bool undated = false) =>
        new(Profile.Email, "plus", [new("codex", "Codex", [
            new(100 - weekly, 10080, undated ? null : weeklyReset ?? WeeklyReset),
            new(100 - shortRemaining, 300, ShortReset)])], null, null, Start.AddMinutes(minutes));
}
