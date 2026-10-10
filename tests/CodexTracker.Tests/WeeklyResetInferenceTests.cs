using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class WeeklyResetInferenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AClearDropToALowValueBracketsTheLatestReset()
    {
        var readings = new[] { (Start, 40d), (Start.AddHours(1), 62d), (Start.AddHours(3), 2d), (Start.AddHours(4), 5d) };
        Assert.Equal(new WeeklyResetInference.Bracket(Start.AddHours(1), Start.AddHours(3)), WeeklyResetInference.LatestReset(readings));
        var twoResets = readings.Concat([(Start.AddDays(7), 70d), (Start.AddDays(7).AddMinutes(15), 0d)]);
        Assert.Equal(new WeeklyResetInference.Bracket(Start.AddDays(7), Start.AddDays(7).AddMinutes(15)), WeeklyResetInference.LatestReset(twoResets));
    }

    [Fact]
    public void SmallCorrectionsHighValuesWideGapsAndInvalidReadingsAreIgnored()
    {
        Assert.Null(WeeklyResetInference.LatestReset([(Start, 30d), (Start.AddHours(1), 22d)]));
        Assert.Null(WeeklyResetInference.LatestReset([(Start, 90d), (Start.AddHours(1), 45d)]));
        Assert.Null(WeeklyResetInference.LatestReset([(Start, 80d), (Start + WeeklyResetInference.MaximumBracket + TimeSpan.FromMinutes(1), 0d)]));
        Assert.Null(WeeklyResetInference.LatestReset([(Start, double.NaN), (Start.AddHours(1), 0d), (Start.AddHours(2), 140d)]));
        Assert.Null(WeeklyResetInference.LatestReset([]));
        Assert.Null(WeeklyResetInference.Next(null, Start));
    }

    [Fact]
    public void TheNextResetKeepsTheBracketAndMovesByWholeWeeks()
    {
        var observed = new WeeklyResetInference.Bracket(Start, Start.AddMinutes(15));
        Assert.Equal(new WeeklyResetInference.Bracket(Start.AddDays(7), Start.AddDays(7).AddMinutes(15)), WeeklyResetInference.Next(observed, Start.AddMinutes(20)));
        Assert.Equal(new WeeklyResetInference.Bracket(Start.AddDays(14), Start.AddDays(14).AddMinutes(15)), WeeklyResetInference.Next(observed, Start.AddDays(7).AddMinutes(15)));
        Assert.Equal(new WeeklyResetInference.Bracket(Start.AddDays(21), Start.AddDays(21).AddMinutes(15)), WeeklyResetInference.Next(observed, Start.AddDays(16)));
        Assert.Null(WeeklyResetInference.Next(new(Start.AddDays(5), Start.AddDays(5).AddHours(1)), Start));
    }

    [Fact]
    public void AnExactClaudeCodeDatePinsTheProjectionUnlessNewerEvidenceDisagrees()
    {
        var exact = Start.AddHours(7);
        Assert.Null(WeeklyResetInference.Anchor(null, null));
        Assert.Equal(new WeeklyResetInference.Bracket(exact, exact), WeeklyResetInference.Anchor(null, exact));
        var drop = new WeeklyResetInference.Bracket(Start.AddDays(14), Start.AddDays(14).AddHours(9));
        Assert.Same(drop, WeeklyResetInference.Anchor(drop, null));
        // Two weeks later the observed drop still contains the exact moment: the precise date wins.
        Assert.Equal(new WeeklyResetInference.Bracket(exact.AddDays(14), exact.AddDays(14)), WeeklyResetInference.Anchor(drop, exact));
        // Claude moved its schedule: the newer drop no longer matches and wins.
        var moved = new WeeklyResetInference.Bracket(Start.AddDays(14).AddHours(10), Start.AddDays(14).AddHours(12));
        Assert.Same(moved, WeeklyResetInference.Anchor(moved, exact));
        // An exact date newer than the drop is the most recent evidence.
        Assert.Equal(new WeeklyResetInference.Bracket(exact.AddDays(21), exact.AddDays(21)), WeeklyResetInference.Anchor(drop, exact.AddDays(21)));
        var next = WeeklyResetInference.Next(WeeklyResetInference.Anchor(null, exact), exact.AddMinutes(1))!;
        Assert.Equal(exact.AddDays(7), next.After); Assert.Equal(exact.AddDays(7), next.By);
    }

    [Fact]
    public void OnlyPastDatesGivenByClaudeCodeAnchorTheWeek()
    {
        var id = Guid.NewGuid(); var now = Start.AddDays(2);
        UsageSample Sample(DateTimeOffset at, DateTimeOffset? reset, bool estimated = false) => new(id, at, 50, reset, WeeklyResetEstimated: estimated);
        var samples = new[] { Sample(Start, Start.AddDays(1)), Sample(Start.AddHours(1), Start.AddDays(1).AddHours(1), estimated: true),
            Sample(Start.AddHours(2), Start.AddDays(8)), Sample(Start.AddHours(3), null) };
        Assert.Equal(Start.AddDays(1), ClaudeCodeUsageReader.LastExactWeeklyReset(samples, null, now));
        var terminal = Snapshot(Start.AddHours(4), (40, 10080, Start.AddDays(1).AddHours(2)));
        Assert.Equal(Start.AddDays(1).AddHours(2), ClaudeCodeUsageReader.LastExactWeeklyReset(samples, terminal, now));
        var estimated = Snapshot(Start.AddHours(4), new QuotaWindow(40, 10080, Start.AddDays(1).AddHours(3), Start.AddDays(1).AddHours(2)));
        Assert.Equal(Start.AddDays(1), ClaudeCodeUsageReader.LastExactWeeklyReset(samples, estimated, now));
        Assert.Null(ClaudeCodeUsageReader.LastExactWeeklyReset(samples, null, Start.AddHours(12)));
        Assert.Null(ClaudeCodeUsageReader.LastExactWeeklyReset([], null, now));
    }

    [Fact]
    public void HistoryRemembersWhetherTheWeeklyDateWasProjected()
    {
        var id = Guid.NewGuid();
        var measured = UsageAnalytics.FromSnapshot(id, Snapshot(Start, (20, 10080, Start.AddDays(3))));
        Assert.False(measured.WeeklyResetEstimated);
        var projected = UsageAnalytics.FromSnapshot(id, Snapshot(Start.AddMinutes(1), new QuotaWindow(20, 10080, Start.AddDays(3), Start.AddDays(3).AddMinutes(-5))));
        Assert.True(projected.WeeklyResetEstimated);
        // Same two-minute bucket and date, different origin: both points are kept.
        Assert.Equal(2, UsageAnalytics.Append(UsageAnalytics.Append([], measured), projected).Count);
        var older = System.Text.Json.JsonSerializer.Deserialize<UsageSample>("{\"AccountId\":\"" + id + "\",\"Timestamp\":\"2026-10-01T08:00:00+00:00\",\"WeeklyRemaining\":50,\"WeeklyResetsAt\":\"2026-10-04T08:00:00+00:00\"}")!;
        Assert.False(older.WeeklyResetEstimated);
    }

    [Fact]
    public void ATerminalDateForTheRunningPeriodSurvivesANewerDesktopReading()
    {
        var terminal = Snapshot(Start, (40, 300, Start.AddHours(2)), (30, 10080, Start.AddDays(3)));
        var desktop = Snapshot(Start.AddMinutes(30), (45, 300, null), (31, 10080, null));
        var merged = ClaudeCodeUsageReader.KeepTerminalResets(desktop, terminal);
        Assert.Equal(Start.AddHours(2), merged.Short!.ResetsAt);
        Assert.Equal(Start.AddDays(3), merged.Weekly!.ResetsAt);
        Assert.False(merged.Weekly.IsResetEstimated);
        Assert.Equal(45, merged.Short.UsedPercent);
        var later = Snapshot(Start.AddHours(3), (5, 300, null), (33, 10080, null));
        var expired = ClaudeCodeUsageReader.KeepTerminalResets(later, terminal);
        Assert.Null(expired.Short!.ResetsAt);
        Assert.Equal(Start.AddDays(3), expired.Weekly!.ResetsAt);
        Assert.Same(terminal, ClaudeCodeUsageReader.KeepTerminalResets(terminal, terminal));
    }

    [Fact]
    public void AnInferredWeeklyResetIsMarkedAndNeverReplacesAKnownDate()
    {
        var bracket = new WeeklyResetInference.Bracket(Start.AddDays(7), Start.AddDays(7).AddMinutes(15));
        var inferred = ClaudeCodeUsageReader.WithWeeklyReset(Snapshot(Start, (10, 300, null), (20, 10080, null)), bracket);
        Assert.True(inferred.Weekly!.IsResetEstimated);
        Assert.Equal(bracket.After, inferred.Weekly.EstimatedResetFrom);
        Assert.Equal(bracket.By, inferred.Weekly.ResetsAt);
        Assert.Null(inferred.Short!.ResetsAt);
        var known = Snapshot(Start, (20, 10080, Start.AddDays(2)));
        Assert.Equal(Start.AddDays(2), ClaudeCodeUsageReader.WithWeeklyReset(known, bracket).Weekly!.ResetsAt);
        Assert.False(ClaudeCodeUsageReader.WithWeeklyReset(known, bracket).Weekly!.IsResetEstimated);
    }

    [Fact]
    public void AnEstimatedFullQuotaRearmsThresholdsLikeAnUndatedOne()
    {
        var profile = new AccountProfile(Guid.NewGuid(), "pace@example.test", AccountProvider.ClaudeCode);
        var state = QuotaAlertEvaluator.Observe(profile, Snapshot(Start, (70, 10080, null)), null).State;
        state = QuotaAlertEvaluator.Observe(profile, Snapshot(Start.AddMinutes(2), (85, 10080, null)), state).State;
        var estimated = new QuotaWindow(0, 10080, Start.AddDays(7), Start.AddDays(7).AddMinutes(-2));
        state = QuotaAlertEvaluator.Observe(profile, Snapshot(Start.AddMinutes(4), estimated), state).State;
        var again = QuotaAlertEvaluator.Observe(profile, Snapshot(Start.AddMinutes(6), new QuotaWindow(85, 10080, Start.AddDays(7), Start.AddDays(7).AddMinutes(-2))), state);
        Assert.Equal(20, Assert.Single(again.Notifications).Threshold);
    }

    private static AccountSnapshot Snapshot(DateTimeOffset at, params (double Used, int Minutes, DateTimeOffset? Reset)[] windows) =>
        new("claude@example.test", "max", [new("claude", "Claude Code", windows.Select(w => new QuotaWindow(w.Used, w.Minutes, w.Reset)).ToArray())], null, null, at);
    private static AccountSnapshot Snapshot(DateTimeOffset at, params QuotaWindow[] windows) =>
        new("claude@example.test", "max", [new("claude", "Claude Code", windows)], null, null, at);
}
