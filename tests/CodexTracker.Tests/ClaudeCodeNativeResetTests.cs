using System.Text;
using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeCodeNativeResetTests
{
    private static readonly Guid Organization = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid SessionId = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly ClaudeCodeIdentity Identity = new("native@example.test", ClaudeFixture.First + "/" + Organization, "max", null);

    private static string Record(DateTimeOffset? observed = null, DateTimeOffset? reset = null, string? invalid = null)
    {
        var info = new Dictionary<string, object?> { ["status"] = "rejected", ["rateLimitType"] = "five_hour",
            ["resetsAt"] = (reset ?? Now.AddHours(2)).ToUnixTimeSeconds() };
        var record = new Dictionary<string, object?> { ["type"] = "assistant", ["error"] = "rate_limit",
            ["isApiErrorMessage"] = true, ["apiErrorStatus"] = 429, ["version"] = "2.1.293", ["sessionId"] = SessionId,
            ["timestamp"] = (observed ?? Now.AddMinutes(-1)).ToString("O"), ["apiErrorParams"] = new { rate_limit_info = info },
            // A representative state from an earlier request must never replace this response's own metadata.
            ["quotaLimits"] = new { status = "rejected", rateLimitType = "seven_day", resetsAt = Now.AddDays(4).ToUnixTimeSeconds() },
            ["message"] = new { content = new[] { new { type = "text", text = "fixture-private-conversation-never-persist" } } } };
        switch (invalid)
        {
            case "weekly": info["rateLimitType"] = "seven_day"; break;
            case "unknown-window": info["rateLimitType"] = "five_hour_overage"; break;
            case "not-rejected": info["status"] = "allowed"; break;
            case "not-rate-error": record["error"] = "billing_error"; break;
            case "user-text": record["type"] = "user"; break;
            case "not-api-error": record["isApiErrorMessage"] = false; break;
            case "wrong-status": record["apiErrorStatus"] = 503; break;
            case "unsupported-version": record["version"] = "2.2.0"; break;
            case "other-session": record["sessionId"] = Guid.NewGuid(); break;
            case "representative-only": record.Remove("apiErrorParams"); break;
            case "string-epoch": info["resetsAt"] = info["resetsAt"]!.ToString(); break;
            case "millisecond-epoch": info["resetsAt"] = Now.AddHours(2).ToUnixTimeMilliseconds(); break;
            case "no-zone": record["timestamp"] = Now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss"); break;
        }
        return JsonSerializer.Serialize(record);
    }

    private static ClaudeCodeNativeResetReader.Reading? Parse(string text, DateTimeOffset? now = null) =>
        ClaudeCodeNativeResetReader.Tail(Encoding.UTF8.GetBytes(text + "\n"), false, SessionId, now ?? Now, default);

    [Fact]
    public void OwnFiveHourErrorMetadataGivesAnExactDeadlineAndContainsNoConversation()
    {
        var reading = Parse(Record())!;
        Assert.Equal(Now.AddHours(2), reading.ResetsAt);
        Assert.Equal(Now.AddMinutes(-1), reading.ObservedAt);
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(reading));
    }

    [Theory]
    [InlineData("weekly")] [InlineData("unknown-window")] [InlineData("not-rejected")]
    [InlineData("not-rate-error")] [InlineData("user-text")] [InlineData("not-api-error")]
    [InlineData("wrong-status")] [InlineData("unsupported-version")] [InlineData("other-session")]
    [InlineData("representative-only")] [InlineData("string-epoch")] [InlineData("millisecond-epoch")]
    [InlineData("no-zone")]
    public void UnsupportedUnboundAndNonQuotaRecordsCannotProvideDates(string invalid) => Assert.Null(Parse(Record(invalid: invalid)));

    [Fact]
    public void ExpiredFutureAndImpossibleDeadlinesAreRejectedWithoutProjection()
    {
        Assert.Null(Parse(Record(reset: Now)));
        Assert.Null(Parse(Record(reset: Now.AddSeconds(-1))));
        Assert.Null(Parse(Record(observed: Now.AddSeconds(1))));
        Assert.Null(Parse(Record(observed: Now.AddHours(-6))));
        Assert.Null(Parse(Record(reset: Now.AddHours(6))));
        Assert.Null(Parse(Record(observed: Now.AddHours(-4), reset: Now.AddHours(2))));
    }

    [Fact]
    public void DuplicateMetadataMalformedAndTruncatedRecordsAreIgnored()
    {
        var valid = Record();
        Assert.Null(Parse(valid.Replace("\"type\":\"assistant\"", "\"type\":\"user\",\"type\":\"assistant\"")));
        Assert.Null(Parse(valid.Replace("\"resetsAt\":", "\"resetsAt\":0,\"resetsAt\":")));
        Assert.Null(Parse(valid[..^3]));
        Assert.Null(Parse(valid + valid));
        Assert.Null(Parse("not-json"));
        Assert.Null(ClaudeCodeNativeResetReader.Tail(Encoding.UTF8.GetBytes(valid), false, SessionId, Now, default));
    }

    [Fact]
    public void OnlyCompleteBoundedLinesAreReadAndTheNewestObservationWins()
    {
        var records = "truncated-tail-prefix\n" + Record(observed: Now.AddMinutes(-2), reset: Now.AddHours(1)) + "\n" +
            Record() + "\n" + Record(observed: Now, reset: Now.AddHours(3));
        Assert.Equal(Now.AddHours(2), ClaudeCodeNativeResetReader.Tail(Encoding.UTF8.GetBytes(records), true, SessionId, Now, default)!.ResetsAt);
        Assert.Null(Parse(Record().Replace("fixture-private-conversation-never-persist", new string('x', ClaudeCodeNativeResetReader.MaximumRecordBytes))));
        Assert.Throws<OperationCanceledException>(() => ClaudeCodeNativeResetReader.Tail(Encoding.UTF8.GetBytes(Record()), false, SessionId, Now, new(true)));
    }

    [Fact]
    public void TextualResetClaimsAndRepresentativeQuotaStateAreNeverUsed()
    {
        var text = JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[] { new { type = "text", text = Record() } } } });
        Assert.Null(Parse(text));
        Assert.Null(Parse(Record(invalid: "representative-only")));
    }

    [Fact]
    public void ADateDoesNotRefreshPercentagesOrWeeklyDatesAndCannotOverrideANewerMeasurement()
    {
        var snapshot = Snapshot(Now.AddMinutes(-10), 40);
        var applied = ClaudeCodeNativeResetReader.Apply(snapshot, Parse(Record()), Now);
        Assert.Equal(Now.AddHours(2), applied.Short!.ResetsAt);
        Assert.Equal(snapshot.FetchedAt, applied.FetchedAt);
        Assert.Equal(snapshot.Short!.UsedPercent, applied.Short.UsedPercent);
        Assert.Equal(snapshot.Weekly, applied.Weekly);
        var newer = snapshot with { FetchedAt = Now };
        Assert.Same(newer, ClaudeCodeNativeResetReader.Apply(newer, Parse(Record()), Now));
        var exhausted = Snapshot(Now, 100);
        Assert.Equal(Now.AddHours(2), ClaudeCodeNativeResetReader.Apply(exhausted, Parse(Record()), Now).Short!.ResetsAt);
        var dated = Snapshot(Now.AddMinutes(-10), 40, Now.AddHours(1));
        Assert.Same(dated, ClaudeCodeNativeResetReader.Apply(dated, Parse(Record()), Now));
        var expired = Snapshot(Now.AddMinutes(-10), 40, Now.AddMinutes(-1));
        Assert.Equal(Now.AddHours(2), ClaudeCodeNativeResetReader.Apply(expired, Parse(Record()), Now).Short!.ResetsAt);
    }

    private static AccountSnapshot Snapshot(DateTimeOffset at, double used, DateTimeOffset? reset = null) =>
        new(Identity.Email, Identity.PlanType, [new("claude", "Claude Code", [new(used, 300, reset), new(20, 10080, Now.AddDays(2))])], null, null, at);

    [Fact]
    public void TheAccountOrganizationAndSessionMustAllMatchAndNativeFilesRemainUnmodified()
    {
        using var fixture = new NativeFixture();
        fixture.Add(Record());
        var files = Directory.EnumerateFiles(fixture.Directory.Root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal(Now.AddHours(2), fixture.Read()!.ResetsAt);
        Assert.Null(fixture.Read(Identity with { AccountId = ClaudeFixture.Second + "/" + Organization }));
        Assert.Null(fixture.Read(Identity with { AccountId = ClaudeFixture.First + "/" + ClaudeFixture.Second }));
        Assert.Null(fixture.Read(Identity with { AccountId = "../unsafe" }));
        Assert.Equal(files.Keys, Directory.EnumerateFiles(fixture.Directory.Root, "*", SearchOption.AllDirectories));
        foreach (var (path, bytes) in files) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(fixture.Directory.File(".credentials.json")));
    }

    [Fact]
    public void ATranscriptAloneCannotAssociateAnAccountAndTheSessionMetadataCannotRedirectFiles()
    {
        using var fixture = new NativeFixture(); fixture.Add(Record());
        File.Delete(fixture.Metadata);
        Assert.Null(fixture.Read());
        File.WriteAllText(fixture.Metadata, "{\"sessionId\":\"wrong-name\",\"cliSessionId\":\"" + SessionId + "\"}");
        Assert.Null(fixture.Read());
        File.WriteAllText(fixture.Metadata, "{\"sessionId\":\"" + Path.GetFileNameWithoutExtension(fixture.Metadata) + "\",\"cliSessionId\":\"../private.json\"}");
        Assert.Null(fixture.Read());
    }

    [Fact]
    public void AppendsAreReadButOnlyTheBoundedTailAndKnownVersionCanProvideADate()
    {
        using var fixture = new NativeFixture(); fixture.Add(Record());
        File.WriteAllText(fixture.Transcript, Record() + "\n" + new string('x', ClaudeCodeNativeResetReader.MaximumTailBytes + 2) + "\n");
        Assert.Null(fixture.Read());
        File.AppendAllText(fixture.Transcript, Record() + "\n");
        Assert.Equal(Now.AddHours(2), fixture.Read()!.ResetsAt);
        File.WriteAllText(fixture.Transcript, Record(invalid: "unsupported-version") + "\n");
        Assert.Null(fixture.Read());
        Assert.Throws<OperationCanceledException>(() => fixture.Read(token: new(true)));
    }

    [Fact]
    public void ANewerExpiredNativeWindowCannotReviveAnOlderFutureDeadline()
    {
        using var fixture = new NativeFixture();
        fixture.Add(Record(observed: Now.AddHours(-2), reset: Now.AddHours(1)));
        File.AppendAllText(fixture.Transcript, Record(observed: Now.AddHours(-1), reset: Now.AddMinutes(-10)) + "\n");
        Assert.Null(fixture.Read());
    }

    [Fact]
    public async Task TrackerImportsTheNativeFallbackAutomaticallyWithoutCredentialsAndPreservesItAfterRestart()
    {
        using var fixture = new NativeFixture();
        ClaudeFixture.SignIn(fixture.Directory, email: Identity.Email); File.Delete(fixture.Directory.File(".credentials.json"));
        var at = DateTimeOffset.UtcNow.AddMinutes(-2); var reset = DateTimeOffset.FromUnixTimeSeconds(at.AddHours(2).ToUnixTimeSeconds());
        fixture.Add(Record(observed: at, reset: reset));
        File.WriteAllText(fixture.HistoryPath, JsonSerializer.Serialize(new { version = 2, samples = new[] {
            new { t = at.AddMinutes(-1).ToUnixTimeMilliseconds(), org = Organization, u = new { fh = 80, sd = 20 } } } }));
        var options = new TrackerServiceOptions { DataDirectory = fixture.Directory.File("tracker"),
            ClaudeLocation = ClaudeFixture.Location(fixture.Directory), ClaudeDesktopUsagePaths = [fixture.HistoryPath],
            AutomaticRefresh = false, MonitorAuthChanges = false };
        await using (var service = new TrackerService(fixture.Directory.File("missing-auth.json"), options))
        {
            await service.InitializeAsync();
            var snapshot = service.State.ActiveAccount!.Snapshot!;
            Assert.Equal(reset, snapshot.Short!.ResetsAt);
            Assert.Equal(80, snapshot.Short.UsedPercent);
            Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(at.AddMinutes(-1).ToUnixTimeMilliseconds()), snapshot.FetchedAt);
            Assert.False(snapshot.Short.IsResetEstimated);
            Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(((ITrackerService)service).ExportProfiles()));
        }
        File.Delete(fixture.Transcript);
        await using var restarted = new TrackerService(fixture.Directory.File("missing-auth.json"), options);
        await restarted.InitializeAsync();
        Assert.Equal(reset, restarted.State.ActiveAccount!.Snapshot!.Short!.ResetsAt);
    }

    private sealed class NativeFixture : IDisposable
    {
        internal TestDirectory Directory { get; } = new();
        internal string HistoryPath => Directory.File("desktop/plan-usage-history.json");
        internal string Metadata => Directory.File("desktop/claude-code-sessions/" + ClaudeFixture.First + "/" + Organization + "/local_66666666-6666-4666-8666-666666666666.json");
        internal string Transcript => Directory.File("projects/fixture/" + SessionId + ".jsonl");
        internal void Add(string record)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Metadata)!);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Transcript)!);
            File.WriteAllText(Metadata, JsonSerializer.Serialize(new { sessionId = Path.GetFileNameWithoutExtension(Metadata), cliSessionId = SessionId,
                cwd = "../fixture-private-path-never-use", message = "fixture-private-conversation-never-persist" }));
            File.WriteAllText(Transcript, record + "\n");
        }
        internal ClaudeCodeNativeResetReader.Reading? Read(ClaudeCodeIdentity? identity = null, CancellationToken token = default) =>
            ClaudeCodeNativeResetReader.Read(ClaudeFixture.Location(Directory), [HistoryPath], identity ?? Identity, Now, token);
        public void Dispose() => Directory.Dispose();
    }
}
