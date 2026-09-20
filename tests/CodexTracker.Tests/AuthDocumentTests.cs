using System.Text;
using System.Text.Json;
using CodexTracker.Codex;
using Xunit;

namespace CodexTracker.Tests;

public sealed class AuthDocumentTests
{
    [Fact]
    public void ParsesOnlySyntheticIdentityClaims()
    {
        var document = AuthDocument.Parse(TestFixtures.Auth());
        Assert.Equal("demo@example.test", document.Email);
        Assert.Equal("test-account-fixture", document.AccountId);
        Assert.Equal("plus", document.PlanType);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"OPENAI_API_KEY\":\"fixture-not-a-real-key\"}")]
    [InlineData("{\"tokens\":null}")]
    [InlineData("{\"tokens\":{\"access_token\":\"not-a-jwt\"}}")]
    public void UnsupportedSessionFormatsProduceControlledErrors(string json) =>
        Assert.Throws<TrackerException>(() => AuthDocument.Parse(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void NonObjectJwtClaimsProduceControlledErrors(string claims)
    {
        var jwt = "fixture." + Convert.ToBase64String(Encoding.UTF8.GetBytes(claims)).TrimEnd('=') + ".fixture";
        var auth = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new { access_token = jwt, id_token = jwt, account_id = "fixture-account" }
        });
        Assert.Throws<TrackerException>(() => AuthDocument.Parse(auth));
    }

    [Theory]
    [InlineData("prolite", 5)]
    [InlineData("pro", 20)]
    [InlineData(" PROLITE ", 5)]
    [InlineData("plus", null)]
    [InlineData("business", null)]
    [InlineData(null, null)]
    public void MultiplierOnlyUsesExplicitlyKnownPlanTypes(string? plan, int? multiplier) =>
        Assert.Equal(multiplier, AuthDocument.PlanMultiplier(plan));

    [Theory]
    [InlineData("2026-09-20T12:30:00Z", "2026-09-20T12:30:00Z")]
    [InlineData("2026-09-20T12:30:00.1234567Z", "2026-09-20T12:30:00.1234567Z")]
    [InlineData("2026-09-20T14:30:00+02:00", "2026-09-20T12:30:00Z")]
    public void SubscriptionDatesPreservePreciseInstantsFromIdToken(string source, string expected)
    {
        var auth = AuthWithSubscription(source, source);
        var document = AuthDocument.Parse(auth);
        var utc = DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(utc, document.SubscriptionStartedAt);
        Assert.Equal(utc, document.SubscriptionEndsAt);
        Assert.Equal(TimeSpan.Zero, document.SubscriptionStartedAt!.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("2026-09-20")]
    [InlineData("2026-09-20T12:30:00")]
    [InlineData("1789907400")]
    public void UnknownSubscriptionDatesDoNotFallBackToTokenIssueOrExpiry(string? value)
    {
        var document = AuthDocument.Parse(AuthWithSubscription(value, value));
        Assert.Null(document.SubscriptionStartedAt);
        Assert.Null(document.SubscriptionEndsAt);
    }

    [Fact]
    public void NumericSubscriptionDatesRemainUnknown()
    {
        var document = AuthDocument.Parse(AuthWithSubscription(1789907400L, 1792499400L));
        Assert.Null(document.SubscriptionStartedAt);
        Assert.Null(document.SubscriptionEndsAt);
    }

    [Fact]
    public void AccessTokenSubscriptionClaimsCannotBecomeSubscriptionDates()
    {
        var document = AuthDocument.Parse(AuthWithSubscription("2026-09-20T12:30:00Z", "2026-10-20T12:30:00Z", includeIdToken: false));
        Assert.Null(document.SubscriptionStartedAt);
        Assert.Null(document.SubscriptionEndsAt);
    }

    private static byte[] AuthWithSubscription(object? startedAt, object? endsAt, bool includeIdToken = true)
    {
        var claims = new Dictionary<string, object?>
        {
            ["email"] = "demo@example.test",
            ["iat"] = 1789907400L,
            ["exp"] = 1792499400L,
            ["https://api.openai.com/auth"] = new Dictionary<string, object?>
            {
                ["chatgpt_account_id"] = "test-account-fixture",
                ["chatgpt_plan_type"] = "pro",
                ["chatgpt_subscription_active_start"] = startedAt,
                ["chatgpt_subscription_active_until"] = endsAt
            }
        };
        var jwt = "fixture." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fixture";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new { access_token = jwt, id_token = includeIdToken ? jwt : null, account_id = "test-account-fixture" }
        });
    }
}
