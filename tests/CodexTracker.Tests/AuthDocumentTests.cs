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
}
