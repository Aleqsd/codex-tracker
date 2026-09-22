using System.Text;
using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class CodexCompatibilityTests
{
    [Fact]
    public void CustomHomeWinsAndExplicitAuthPathWinsOverEnvironmentWithoutCreatingDirectories()
    {
        using var directory = new TestDirectory();
        var custom = directory.File("custom home");
        var profile = directory.File("fictional-user");
        var fromEnvironment = CodexAuthLocation.Resolve(null, custom, profile);
        Assert.Equal(Path.Combine(custom, "auth.json"), fromEnvironment.Path);
        Assert.Equal(CodexHomeSource.Environment, fromEnvironment.Source);
        var defaultHome = CodexAuthLocation.Resolve(null, null, profile);
        Assert.Equal(Path.Combine(profile, ".codex", "auth.json"), defaultHome.Path);
        Assert.Equal(CodexHomeSource.Default, defaultHome.Source);
        var explicitPath = directory.File("fixture-auth.json");
        var explicitHome = CodexAuthLocation.Resolve(explicitPath, custom, profile);
        Assert.Equal(explicitPath, explicitHome.Path);
        Assert.Equal(CodexHomeSource.Explicit, explicitHome.Source);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Root));
    }

    [Fact]
    public void InvalidCustomHomeNeverFallsBackToAnotherAccount()
    {
        var location = CodexAuthLocation.Resolve(null, "invalid\0home", "C:\\fictional-user");
        Assert.Null(location.Path);
        Assert.Equal(CodexHomeSource.Environment, location.Source);
    }

    [Fact]
    public void LocatorSkipsMalformedPathAndSelectsNativeExecutableWithoutRunningIt()
    {
        using var directory = new TestDirectory();
        var native = directory.File("native directory");
        Directory.CreateDirectory(native);
        var executable = Path.Combine(native, "codex.exe");
        File.WriteAllText(executable, "this is not executable code");
        File.WriteAllText(directory.File("codex.cmd"), "must never run");
        var result = CodexLocator.FindExecutable($"invalid\0path;\"{native}\"", directory.File("local"), directory.File("roaming"));
        Assert.Equal(executable, result);
    }

    [Fact]
    public void LocatorFindsNativeNpmPackageAndMissingInstallationReturnsNull()
    {
        using var directory = new TestDirectory();
        var local = directory.File("local"); var roaming = directory.File("roaming");
        Assert.Null(CodexLocator.FindExecutable(null, local, roaming));
        var native = Path.Combine(roaming, "npm", "node_modules", "@openai", "codex", "vendor", "windows", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "fixture");
        Assert.Equal(native, CodexLocator.FindExecutable(null, local, roaming));
    }

    [Theory]
    [InlineData(null, CodexSessionStatus.Missing, CodexFailureCode.SessionMissing)]
    [InlineData("{broken", CodexSessionStatus.Unsupported, CodexFailureCode.UnsupportedSession)]
    [InlineData("{\"OPENAI_API_KEY\":\"fixture-private-key\"}", CodexSessionStatus.Unsupported, CodexFailureCode.UnsupportedSession)]
    public async Task FirstLaunchDistinguishesMissingAndUnsupportedSessionWithoutReadingQuotas(string? content,
        CodexSessionStatus session, CodexFailureCode failure)
    {
        using var directory = new TestDirectory();
        var auth = directory.File("private-auth.json");
        if (content is not null) await File.WriteAllTextAsync(auth, content);
        await using var service = new TrackerService(auth, Options(directory));
        await service.InitializeAsync();
        var diagnostic = service.GetCompatibilityDiagnostic();
        Assert.Equal(session, diagnostic.Session);
        Assert.Equal(failure, diagnostic.LastFailure);
        Assert.Equal(CodexServiceStatus.NotChecked, diagnostic.Service);
        Assert.NotNull(diagnostic.CheckedAt);
        Assert.Null(diagnostic.LastSuccessfulReadAt);
        Assert.Empty(service.State.Accounts);
        Assert.NotEmpty(service.State.StatusMessage!);
        var serialized = JsonSerializer.Serialize(diagnostic);
        Assert.DoesNotContain(directory.Root, serialized);
        Assert.DoesNotContain("private", serialized);
        Assert.DoesNotContain("fixture", serialized);
        Assert.DoesNotContain("fixture-private-key", service.State.StatusMessage!);
    }

    [Fact]
    public async Task InvalidSessionPathProducesActionableStateInsteadOfFailingStartup()
    {
        using var directory = new TestDirectory();
        await using var service = new TrackerService("invalid\0auth", Options(directory));
        await service.InitializeAsync();
        Assert.Equal(CodexSessionStatus.InvalidHome, service.GetCompatibilityDiagnostic().Session);
        Assert.Contains("CODEX_HOME", service.State.StatusMessage);
        Assert.Empty(service.State.Accounts);
    }

    [Fact]
    public async Task MissingCodexExecutableKeepsKnownIdentityAndExplainsHowToRecover()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        await File.WriteAllBytesAsync(auth, TestFixtures.Auth());
        await using var service = new TrackerService(auth, Options(directory));
        await service.InitializeAsync();
        var diagnostic = service.GetCompatibilityDiagnostic();
        Assert.Equal(CodexSessionStatus.Detected, diagnostic.Session);
        Assert.Equal(CodexServiceStatus.Missing, diagnostic.Service);
        Assert.Equal(CodexFailureCode.CodexNotFound, diagnostic.LastFailure);
        Assert.Null(diagnostic.LastSuccessfulReadAt);
        var account = Assert.Single(service.State.Accounts);
        Assert.True(account.IsActiveInCodex);
        Assert.Null(account.Snapshot);
        Assert.Contains("installez", account.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpiredAccessTokenKeepsActiveIdentityAndNeverRenewsOrChangesCodexSession()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        var original = SessionWithExpiry(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());
        await File.WriteAllBytesAsync(auth, original);
        using var lease = new FileStream(auth, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var service = new TrackerService(auth, Options(directory));
        await service.InitializeAsync();
        var diagnostic = service.GetCompatibilityDiagnostic();
        Assert.Equal(CodexSessionStatus.Expired, diagnostic.Session);
        Assert.Equal(CodexFailureCode.SessionExpired, diagnostic.LastFailure);
        Assert.Equal(CodexServiceStatus.NotChecked, diagnostic.Service);
        var account = Assert.Single(service.State.Accounts);
        Assert.True(account.IsActiveInCodex);
        Assert.Null(account.Snapshot);
        Assert.Contains("renouvelle", account.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(auth));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(Options(directory).DataDirectory, "observer-runtime")));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    [InlineData(long.MaxValue)]
    public void MissingOrUnusableExpiryRemainsUnknown(object? expiry)
    {
        Assert.Null(AuthDocument.Parse(SessionWithExpiry(expiry)).AccessTokenExpiresAt);
    }

    [Fact]
    public void SessionExpiryUsesAccessTokenRatherThanAnOlderIdentityToken()
    {
        var expected = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        using var fresh = JsonDocument.Parse(SessionWithExpiry(expected));
        using var old = JsonDocument.Parse(SessionWithExpiry(1));
        var session = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new
            {
                access_token = fresh.RootElement.GetProperty("tokens").GetProperty("access_token").GetString(),
                id_token = old.RootElement.GetProperty("tokens").GetProperty("id_token").GetString()
            }
        });
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expected), AuthDocument.Parse(session).AccessTokenExpiresAt);
    }

    [Theory]
    [InlineData(-32601, CodexFailureCode.ProtocolUnsupported)]
    [InlineData(-32602, CodexFailureCode.ProtocolUnsupported)]
    [InlineData(-32000, CodexFailureCode.ServiceUnavailable)]
    public void ProtocolFailuresHaveControlledCodesWithoutServerErrorBodies(int rpcCode, CodexFailureCode expected)
    {
        var error = AppServerClient.RpcFailure(rpcCode);
        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulObservationAndLaterFailureRetainThePreciseLastSuccessWithoutPrivateData()
    {
        using var directory = new TestDirectory();
        var auth = directory.File("auth.json");
        await File.WriteAllBytesAsync(auth, TestFixtures.Auth("private@example.test", "private-session"));
        var reader = new DiagnosticReader();
        await using var service = new TrackerService(auth, Options(directory), reader);
        await service.InitializeAsync();
        var good = service.GetCompatibilityDiagnostic();
        Assert.Equal(CodexServiceStatus.Compatible, good.Service);
        Assert.Equal(reader.ObservedAt, good.LastSuccessfulReadAt);
        reader.Failure = new TrackerException("A controlled protocol error.", CodexFailureCode.ProtocolUnsupported);
        await service.RefreshAsync();
        var failed = service.GetCompatibilityDiagnostic();
        Assert.Equal(CodexServiceStatus.Incompatible, failed.Service);
        Assert.Equal(reader.ObservedAt, failed.LastSuccessfulReadAt);
        Assert.Equal(CodexFailureCode.ProtocolUnsupported, failed.LastFailure);
        var serialized = JsonSerializer.Serialize(failed);
        Assert.DoesNotContain("private", serialized);
        Assert.DoesNotContain("example.test", serialized);
        Assert.DoesNotContain(directory.Root, serialized);
        Assert.DoesNotContain("controlled", serialized);
    }

    private static TrackerServiceOptions Options(TestDirectory directory) => new()
    {
        DataDirectory = directory.File("tracker-data"), CodexExecutablePath = directory.File("absent-codex.exe"),
        AutomaticRefresh = false, MonitorAuthChanges = false
    };

    private static byte[] SessionWithExpiry(object? expiry)
    {
        var claims = new Dictionary<string, object?>
        {
            ["email"] = "fixture@example.test", ["exp"] = expiry,
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = "fixture-account", chatgpt_plan_type = "plus" }
        };
        var jwt = "fixture." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_') + ".fixture";
        return JsonSerializer.SerializeToUtf8Bytes(new { tokens = new { access_token = jwt, id_token = jwt } });
    }

    private sealed class DiagnosticReader : IAccountUsageReader
    {
        public DateTimeOffset ObservedAt { get; } = DateTimeOffset.UtcNow;
        public TrackerException? Failure { get; set; }
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken)
            => Failure is not null ? Task.FromException<AccountSnapshot>(Failure)
                : Task.FromResult(new AccountSnapshot(profile.Email, "plus", [], null, null, ObservedAt));
    }
}
