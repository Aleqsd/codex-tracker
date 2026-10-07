using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

internal static class ClaudeFixture
{
    internal static readonly Guid First = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid Second = Guid.Parse("22222222-2222-4222-8222-222222222222");
    internal static ClaudeCodeLocation Location(TestDirectory directory) => new(directory.File(".claude.json"), directory.File(".credentials.json"));
    internal static void SignIn(TestDirectory directory, string email = "demo@example.test", Guid? id = null, DateTimeOffset? expires = null,
        string organization = "33333333-3333-4333-8333-333333333333", string? organizationName = null, string? organizationType = null)
    {
        File.WriteAllText(directory.File(".claude.json"), JsonSerializer.Serialize(new { oauthAccount = new { emailAddress = email,
            accountUuid = (id ?? First).ToString(), organizationUuid = organization, organizationName, organizationType } }));
        File.WriteAllText(directory.File(".credentials.json"), JsonSerializer.Serialize(new { claudeAiOauth = new
        { accessToken = "fixture-secret-never-persist", refreshToken = "fixture-refresh-never-persist", expiresAt = (expires ?? DateTimeOffset.UtcNow.AddHours(2)).ToUnixTimeMilliseconds(), subscriptionType = "max", rateLimitTier = "default_claude_max_20x" } }));
    }
    internal static string Input(Guid session, double percent = 42) => JsonSerializer.Serialize(new { session_id = session, source = "startup",
        cwd = "fixture-private-project-never-persist", transcript_path = "fixture-private-chat-never-persist",
        rate_limits = new { five_hour = new { used_percentage = percent, resets_at = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() },
            seven_day = new { used_percentage = 70, resets_at = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds() } } });
}

public sealed class ClaudeCodeTests
{
    [Fact]
    public void LocationsRespectCustomDirectoryAndNeverFallBackFromInvalidOne()
    {
        using var directory = new TestDirectory();
        var normal = ClaudeCodeLocation.Resolve(null, directory.Root);
        Assert.Equal(directory.File(".claude.json"), normal.ConfigPath);
        Assert.Equal(Path.Combine(directory.Root, ".claude", ".credentials.json"), normal.CredentialsPath);
        var custom = ClaudeCodeLocation.Resolve(directory.File("custom"), directory.Root);
        Assert.Equal(Path.Combine(directory.Root, "custom", ".claude.json"), custom.ConfigPath);
        Assert.Null(ClaudeCodeLocation.Resolve("relative", directory.Root).ConfigPath);
        Assert.Null(ClaudeCodeLocation.Resolve("", directory.Root).CredentialsPath);
    }

    [Fact]
    public async Task IdentityContainsOnlyMetadataAndExpiredSessionsRemainIdentifiable()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory, expires: DateTimeOffset.UtcNow.AddSeconds(-1));
        var identity = await ClaudeCodeIdentity.ReadAsync(ClaudeFixture.Location(directory), default);
        Assert.Equal("demo@example.test", identity.Email); Assert.Equal("max", identity.PlanType); Assert.Equal(20, identity.PlanMultiplier);
        Assert.True(identity.ExpiresAt < DateTimeOffset.UtcNow);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(identity));
        File.WriteAllText(directory.File(".credentials.json"), "{\"apiKey\":\"fixture-secret\"}");
        var desktopOnly = await ClaudeCodeIdentity.ReadAsync(ClaudeFixture.Location(directory), default);
        Assert.False(desktopOnly.HasLocalCredential); Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(desktopOnly));
    }

    [Theory]
    [InlineData(0, 100)] [InlineData(100, 0)] [InlineData(42.5, 57.5)]
    public void StatuslinePercentagesAreUsedQuotaAndUnknownDatesStayUnknown(double used, double remaining)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { rate_limits = new { five_hour = new { used_percentage = used, resets_at = (long?)null } }, context_window = new { remaining_percentage = 99 } }));
        var snapshot = ClaudeCodeQuotaParser.Parse(document.RootElement, "demo@example.test", "pro", DateTimeOffset.UtcNow)!;
        Assert.Equal(remaining, snapshot.Short!.RemainingPercent); Assert.Null(snapshot.Short.ResetsAt);
        Assert.Null(snapshot.Weekly); Assert.Null(snapshot.AvailableResetCredits);
    }

    [Theory]
    [InlineData("{}")] [InlineData("{\"rate_limits\":null}")] [InlineData("{\"rate_limits\":{\"five_hour\":null}}")] [InlineData("{\"context_window\":{\"remaining_percentage\":99}}")]
    public void MissingQuotaNeverBecomesAFullQuota(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Null(ClaudeCodeQuotaParser.Parse(document.RootElement, "demo@example.test", null, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("-1")] [InlineData("101")] [InlineData("\"50\"")] [InlineData("1e300")]
    public void InvalidPercentagesAreRejected(string percent)
    {
        using var document = JsonDocument.Parse("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":" + percent + "}}}");
        Assert.Throws<JsonException>(() => ClaudeCodeQuotaParser.Parse(document.RootElement, "demo@example.test", null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task NativeSessionCaptureIsPrivatePassiveAndRejectsAnOldAccountAfterSwitching()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var location = ClaudeFixture.Location(directory); var data = directory.File("tracker");
        var collector = new ClaudeCodeObservations(data, location); var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var auth = File.ReadAllBytes(location.CredentialsPath!); var config = File.ReadAllBytes(location.ConfigPath!);
        Assert.Null(await collector.CaptureAsync(ClaudeFixture.Input(session), false, now));
        await collector.CaptureAsync(ClaudeFixture.Input(session), true, now);
        Assert.Contains("58%", (await collector.CaptureAsync(ClaudeFixture.Input(session), false, now.AddSeconds(1)))!);
        var identity = await ClaudeCodeIdentity.ReadAsync(location, default);
        var snapshot = collector.Read(identity); Assert.Equal(30, snapshot.Weekly!.RemainingPercent);
        Assert.Equal(now.AddSeconds(1), snapshot.FetchedAt);
        var bytes = Directory.EnumerateFiles(data, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
        Assert.All(bytes, text => { Assert.DoesNotContain("never-persist", text); Assert.DoesNotContain("transcript", text); });
        Assert.Equal(auth, File.ReadAllBytes(location.CredentialsPath!)); Assert.Equal(config, File.ReadAllBytes(location.ConfigPath!));
        Assert.False(File.Exists(Path.Combine(data, "settings.json"))); Assert.False(File.Exists(Path.Combine(data, "preferences.json")));
        ClaudeFixture.SignIn(directory, "second@example.test", ClaudeFixture.Second);
        Assert.Null(await collector.CaptureAsync(ClaudeFixture.Input(session), false, now.AddSeconds(2)));
        var second = await ClaudeCodeIdentity.ReadAsync(location, default);
        Assert.Throws<TrackerException>(() => collector.Read(second));
        await collector.CaptureAsync(ClaudeFixture.Input(session), true, now.AddSeconds(2));
        Assert.Null(await collector.CaptureAsync(ClaudeFixture.Input(session), false, now.AddSeconds(3)));
        session = Guid.NewGuid();
        await collector.CaptureAsync(ClaudeFixture.Input(session), true, now.AddSeconds(2));
        Assert.NotNull(await collector.CaptureAsync(ClaudeFixture.Input(session), false, now.AddSeconds(3)));
        Assert.Equal("second@example.test", collector.Read(second).Email);
    }

    [Fact]
    public async Task CollectorRejectsMalformedInputSilentlyAndNeverOpensAnyTrackerStore()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var output = new StringWriter(); var collector = new ClaudeCodeObservations(directory.File("tracker"), ClaudeFixture.Location(directory));
        await collector.RunAsync(new StringReader("{broken fixture-secret"), output, false);
        Assert.Empty(output.ToString()); Assert.False(Directory.Exists(directory.File("tracker")));
        var configuration = JsonDocument.Parse(ClaudeCodeObservations.Configuration(directory.File("app with '$` spaces.exe")));
        var command = configuration.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1]));
        Assert.Contains("--claude-statusline", script); Assert.Contains("with ''$` spaces.exe", script);
        Assert.DoesNotContain("fixture-secret", command);
    }
}
