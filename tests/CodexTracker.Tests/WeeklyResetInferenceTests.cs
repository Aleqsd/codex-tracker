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
