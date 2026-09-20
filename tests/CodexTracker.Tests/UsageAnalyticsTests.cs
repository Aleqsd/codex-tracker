using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class UsageAnalyticsTests
{
    private static readonly Guid Account = Guid.Parse("5a600eaa-f0fa-4461-9214-c6b2f2bf7d27");
    private static readonly DateTimeOffset Start = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ForecastChoosesTheWindowExpectedToRunOutFirst()
    {
        var samples = Enumerable.Range(0, 5).Select(i => Sample(i * 5, 100 - i * 10, 100 - i * 20)).ToArray();
        var forecast = UsageAnalytics.Estimate(samples, Start.AddMinutes(20));
        Assert.Equal(UsageWindowKind.Short, forecast.Window);
        Assert.Equal(TimeSpan.FromMinutes(5), forecast.TimeToExhaustion);
        Assert.Equal(Start.AddMinutes(25), forecast.EstimatedExhaustionAt);
        Assert.Contains("si le rythme", forecast.Explanation);
    }

    [Fact]
    public void ForecastRequiresAContinuousStableWindowAndFreshData()
    {
        var samples = Enumerable.Range(0, 5).Select(i => Sample(i * 5, 100 - i * 10)).ToArray();
        Assert.NotNull(UsageAnalytics.Estimate(samples, Start.AddMinutes(20)).EstimatedExhaustionAt);
        Assert.Null(UsageAnalytics.Estimate(samples.Take(3).ToArray(), Start.AddMinutes(10)).EstimatedExhaustionAt);
        Assert.Null(UsageAnalytics.Estimate(samples, Start.AddMinutes(26)).EstimatedExhaustionAt);
        Assert.Null(UsageAnalytics.Estimate([samples[0], samples[1], samples[3], samples[4]], Start.AddMinutes(20)).EstimatedExhaustionAt);
        Assert.Null(UsageAnalytics.Estimate([.. samples.Take(4), samples[4] with { WeeklyResetsAt = Start.AddDays(8) }], Start.AddMinutes(20)).EstimatedExhaustionAt);
    }

    [Fact]
    public void ForecastDeclinesFlatIncreasingAndHighlyIrregularConsumption()
    {
        var flat = Enumerable.Range(0, 7).Select(i => Sample(i * 5, 80)).ToArray();
        Assert.Null(UsageAnalytics.Estimate(flat, Start.AddMinutes(30)).EstimatedExhaustionAt);
        var increasing = Enumerable.Range(0, 7).Select(i => Sample(i * 5, 60 + i)).ToArray();
        Assert.Null(UsageAnalytics.Estimate(increasing, Start.AddMinutes(30)).EstimatedExhaustionAt);
        var burst = flat.Select((sample, i) => sample with { WeeklyRemaining = i == 6 ? 30 : 80 }).ToArray();
        Assert.Null(UsageAnalytics.Estimate(burst, Start.AddMinutes(30)).EstimatedExhaustionAt);
    }

    [Fact]
    public void ForecastDoesNotPredictExhaustionAfterTheKnownReset()
    {
        var samples = Enumerable.Range(0, 5).Select(i => Sample(i * 5, 100 - i) with { WeeklyResetsAt = Start.AddHours(1) }).ToArray();
        var forecast = UsageAnalytics.Estimate(samples, Start.AddMinutes(20));
        Assert.Null(forecast.EstimatedExhaustionAt);
        Assert.Contains("reset", forecast.Explanation);
    }

    [Fact]
    public void AppendKeepsLatestPointWithinTwoMinuteBucketAndPreservesResetBoundary()
    {
        IReadOnlyList<UsageSample> history = [];
        history = UsageAnalytics.Append(history, Sample(0, 90));
        history = UsageAnalytics.Append(history, Sample(1, 88));
        Assert.Equal(88, Assert.Single(history).WeeklyRemaining);
        history = UsageAnalytics.Append(history, Sample(2, 85));
        Assert.Equal(2, history.Count);
        history = UsageAnalytics.Append(history, Sample(2.5, 100) with { WeeklyResetsAt = Start.AddDays(8) });
        Assert.Equal(3, history.Count);
        Assert.Same(history, UsageAnalytics.Append(history, Sample(1, 2)));
        Assert.Same(history, UsageAnalytics.Append(history, Sample(3, double.NaN)));
    }

    [Fact]
    public void HistoryHasNinetyDayAndCountBounds()
    {
        var sample = Sample(0, 50);
        var old = Enumerable.Range(0, 70_000).Select(i => sample with { Timestamp = Start.AddMinutes(-140_000 + i * 2) }).ToArray();
        var history = UsageAnalytics.Append(old, sample);
        Assert.True(history.Count <= UsageAnalytics.MaximumSamplesPerAccount);
        Assert.All(history, item => Assert.True(item.Timestamp >= Start - UsageAnalytics.Retention));
        Assert.Equal(sample, history[^1]);
    }

    private static UsageSample Sample(double minutes, double weekly, double? shortRemaining = null) =>
        new(Account, Start.AddMinutes(minutes), weekly, Start.AddDays(7), shortRemaining, shortRemaining is null ? null : Start.AddHours(5));
}
