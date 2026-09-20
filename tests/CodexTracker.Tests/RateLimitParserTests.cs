using System.Text.Json;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class RateLimitParserTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(100, 100, 0)]
    [InlineData(-10, 0, 100)]
    [InlineData(130, 100, 0)]
    [InlineData(12.5, 12.5, 87.5)]
    public void UsageIsClampedAndRemainingIsItsComplement(double input, double used, double remaining)
    {
        var snapshot = Parse(JsonSerializer.Serialize(new
        {
            rateLimits = new { primary = new { usedPercent = input, windowDurationMins = 300 } }
        }));
        var window = Assert.Single(Assert.Single(snapshot.Buckets).Windows);
        Assert.Equal(used, window.UsedPercent);
        Assert.Equal(remaining, window.RemainingPercent);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("secondary")]
    public void WeeklyQuotaIsIdentifiedByDurationInsteadOfPosition(string position)
    {
        var snapshot = Parse("""
        {"rateLimitsByLimitId":{"codex":{"POSITION":{"usedPercent":31,"windowDurationMins":10080,"resetsAt":1789987200}}}}
        """.Replace("POSITION", position, StringComparison.Ordinal));
        Assert.NotNull(snapshot.Weekly);
        Assert.Equal(69, snapshot.Weekly.RemainingPercent);
        Assert.True(snapshot.Weekly.IsWeekly);
    }

    [Fact]
    public void MapIsAuthoritativeAndBucketsAreNotCombined()
    {
        var snapshot = Parse("""
        {
          "rateLimits":{"primary":{"usedPercent":99,"windowDurationMins":10080}},
          "rateLimitsByLimitId":{
            "review":{"limitId":"codex","limitName":"Reviews","primary":{"usedPercent":100,"windowDurationMins":10080}},
            "codex":{"limitName":"Codex","secondary":{"usedPercent":23,"windowDurationMins":10080}},
            "model-a":{"primary":{"usedPercent":50,"windowDurationMins":300}}
          }
        }
        """);
        Assert.Equal(3, snapshot.Buckets.Count);
        Assert.Equal(77, snapshot.Weekly!.RemainingPercent);
        Assert.Equal("review", snapshot.Buckets[0].Id);
        Assert.Equal("Reviews", snapshot.Buckets[0].Name);
    }

    [Fact]
    public void UnrelatedWeeklyBucketCannotBecomeCodexWeeklyQuota()
    {
        var snapshot = Parse("""
        {"rateLimitsByLimitId":{"review":{"primary":{"usedPercent":80,"windowDurationMins":10080}}}}
        """);
        Assert.Null(snapshot.Weekly);
    }

    [Fact]
    public void EmptyAuthoritativeMapDoesNotRestoreLegacyValues()
    {
        var snapshot = Parse("""
        {"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":50,"windowDurationMins":10080}}}
        """);
        Assert.Empty(snapshot.Buckets);
        Assert.Null(snapshot.Weekly);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"usedPercent\":null,\"windowDurationMins\":10080}")]
    [InlineData("{\"usedPercent\":\"50\",\"windowDurationMins\":10080}")]
    public void UnknownUsageDoesNotBecomeZeroOrAvailableQuota(string window)
    {
        var snapshot = Parse("""{"rateLimits":{"primary":WINDOW}}""".Replace("WINDOW", window, StringComparison.Ordinal));
        Assert.Empty(Assert.Single(snapshot.Buckets).Windows);
        Assert.Null(snapshot.Weekly);
    }

    [Fact]
    public void MissingDurationAndResetRemainUnknown()
    {
        var window = Assert.Single(Assert.Single(Parse("""
        {"rateLimits":{"primary":{"usedPercent":1}}}
        """).Buckets).Windows);
        Assert.Null(window.WindowDurationMins);
        Assert.Null(window.ResetsAt);
        Assert.False(window.IsWeekly);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-300")]
    [InlineData("9223372036854775807")]
    public void InvalidOrUnknownDurationDoesNotAcquireAFiveHourDefault(string duration)
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":DURATION}}}""".Replace("DURATION", duration, StringComparison.Ordinal));
        Assert.Null(Assert.Single(Assert.Single(snapshot.Buckets).Windows).WindowDurationMins);
    }

    [Fact]
    public void LegacyViewFallbackKeepsExplicitBucketId()
    {
        var snapshot = Parse("""
        {"rateLimitsByLimitId":null,"rateLimits":{"limitId":"review","primary":{"usedPercent":50,"windowDurationMins":10080}}}
        """);
        Assert.Equal("review", Assert.Single(snapshot.Buckets).Id);
        Assert.Null(snapshot.Weekly);
    }

    [Fact]
    public void AccountPlanWinsAndCodexBucketIsPreferredFallback()
    {
        const string json = """
        {"rateLimitsByLimitId":{"review":{"planType":"free"},"codex":{"planType":"plus"}}}
        """;
        Assert.Equal("pro", Parse(json, "pro").PlanType);
        Assert.Equal("plus", Parse(json).PlanType);
        Assert.Equal("plus", Parse(json, " ").PlanType);
        Assert.Equal("team", Parse("""{"rateLimits":{"planType":"team"}}""").PlanType);
    }

    [Fact]
    public void MissingValuesAreNotRepresentedAsZero()
    {
        var snapshot = Parse("{}");
        Assert.Null(snapshot.PlanType);
        Assert.Null(snapshot.AvailableResetCredits);
        Assert.Null(snapshot.ResetCredits);
        Assert.Empty(snapshot.Buckets);
        Assert.Equal("demo@example.test", snapshot.Email);
        Assert.Equal(FetchedAt, snapshot.FetchedAt);
    }

    [Fact]
    public void AvailableCreditCountIsAuthoritativeWhenDetailsAreCapped()
    {
        var snapshot = Parse("""
        {"rateLimitResetCredits":{"availableCount":7,"credits":[
          {"id":"fixture-credit","title":"Full reset","status":"available","resetType":"codexRateLimits","grantedAt":1787357302,"expiresAt":1789949302}
        ]}}
        """);
        Assert.Equal(7, snapshot.AvailableResetCredits);
        var credit = Assert.Single(snapshot.ResetCredits!);
        Assert.Equal("fixture-credit", credit.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787357302), credit.GrantedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789949302), credit.ExpiresAt);
    }

    [Theory]
    [InlineData("{\"availableCount\":0,\"credits\":null}", 0, null)]
    [InlineData("{\"availableCount\":3}", 3, null)]
    [InlineData("{\"availableCount\":0,\"credits\":[]}", 0, 0)]
    [InlineData("{\"availableCount\":-1}", null, null)]
    [InlineData("{\"availableCount\":null,\"credits\":[]}", null, 0)]
    public void NullAndEmptyCreditDetailsRetainDifferentMeanings(string summary, int? count, int? detailCount)
    {
        var snapshot = Parse("""{"rateLimitResetCredits":SUMMARY}""".Replace("SUMMARY", summary, StringComparison.Ordinal));
        Assert.Equal(count, snapshot.AvailableResetCredits);
        Assert.Equal(detailCount, snapshot.ResetCredits?.Count);
    }

    [Fact]
    public void DetailsWithoutCountDoNotInventAvailableCount()
    {
        var snapshot = Parse("""
        {"rateLimitResetCredits":{"credits":[{"id":"fixture-credit","grantedAt":1787357302,"expiresAt":null}]}}
        """);
        Assert.Null(snapshot.AvailableResetCredits);
        var credit = Assert.Single(snapshot.ResetCredits!);
        Assert.Null(credit.ExpiresAt);
        Assert.Null(credit.Title);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("9223372036854775807")]
    [InlineData("\"2026-09-20\"")]
    public void InvalidResetTimestampRemainsUnknown(string timestamp)
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":5,"resetsAt":TIMESTAMP}}}""".Replace("TIMESTAMP", timestamp, StringComparison.Ordinal));
        Assert.Null(Assert.Single(Assert.Single(snapshot.Buckets).Windows).ResetsAt);
    }

    [Theory]
    [InlineData("2026-03-29T00:30:00Z", 1, 1)]
    [InlineData("2026-03-29T01:30:00Z", 3, 2)]
    [InlineData("2026-10-25T00:30:00Z", 2, 2)]
    [InlineData("2026-10-25T01:30:00Z", 2, 1)]
    public void ExactUtcResetSurvivesParisDaylightSavingTransitions(string isoUtc, int localHour, int offsetHours)
    {
        var utc = DateTimeOffset.Parse(isoUtc, System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = Parse(JsonSerializer.Serialize(new
        {
            rateLimits = new { primary = new { usedPercent = 5, resetsAt = utc.ToUnixTimeSeconds() } }
        }));
        var reset = Assert.Single(Assert.Single(snapshot.Buckets).Windows).ResetsAt!.Value;
        Assert.Equal(utc, reset);
        Assert.Equal(TimeSpan.Zero, reset.Offset);
        var paris = TimeZoneInfo.ConvertTime(reset, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"));
        Assert.Equal(localHour, paris.Hour);
        Assert.Equal(TimeSpan.FromHours(offsetHours), paris.Offset);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void NonObjectResponsesAreRejected(string json) => Assert.Throws<JsonException>(() => Parse(json));

    private static AccountSnapshot Parse(string json, string? plan = null)
    {
        using var document = JsonDocument.Parse(json);
        return RateLimitParser.Parse(document.RootElement, "demo@example.test", plan, FetchedAt);
    }
}
