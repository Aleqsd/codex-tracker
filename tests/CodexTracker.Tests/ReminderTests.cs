using System.Net;
using System.Text;
using System.Text.Json;
using CodexTracker.App;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ReminderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-21T10:00:00Z");
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static TrackerState State(DateTimeOffset? at = null, int? reserves = 1) => new([
        new(new(AccountId, "studio@example.test"), new("studio@example.test", "plus", [new("codex", null, [new(50, 10080, at ?? Now.AddHours(1)), new(10, 300, at ?? Now.AddHours(1))])], reserves,
            [new("credit-A", "Réserve", Now.AddDays(-1), at ?? Now.AddHours(1))], Now.AddHours(-2)))], AccountId);
    private static ReminderRule[] Rule(ReminderChannel channel = ReminderChannel.Windows, ResetKind kind = ResetKind.Weekly) => [new(kind, true, [1440, 60], [channel])];
    private static NotificationSecrets Config => new(new(true, "AC" + new string('a', 32), "SK" + new string('b', 32), "fake-secret", "+33123456789", "+33123456789", "+33612345678"), new(true, "fake-sendgrid-key", "sender@example.test", "to@example.test"));
    private static ReminderOccurrence Occurrence(ReminderChannel channel) => ReminderPlanner.Due(State(), Rule(channel), Now).First();

    [Theory]
    [InlineData(1441, 0)] [InlineData(1440, 1)] [InlineData(61, 1)] [InlineData(60, 2)] [InlineData(1, 2)] [InlineData(0, 0)] [InlineData(-1, 0)]
    public void ThresholdsUseExactServerDate(int minutes, int count) => Assert.Equal(count, ReminderPlanner.Due(State(Now.AddMinutes(minutes)), Rule(), Now).Count);

    [Fact]
    public void MissingWeeklyWindowNeverSubstitutesShortQuota()
    {
        var account = State().Accounts[0];
        account = account with { Snapshot = account.Snapshot! with { Buckets = [new("codex", null, [new(0, 300, Now.AddHours(1))])] } };
        Assert.Empty(ReminderPlanner.Due(new([account], AccountId), Rule(), Now));
    }
    [Theory] [InlineData(null)] [InlineData(0)]
    public void MissingOrEmptyReserveCounterDoesNotNotify(int? count) => Assert.Empty(ReminderPlanner.Due(State(reserves: count), Rule(kind: ResetKind.Reserve), Now));
    [Fact]
    public void CreditsHaveIndependentStableIdsAndAccountsAreIsolated()
    {
        var state = State(); var account = state.Accounts[0];
        account = account with { Snapshot = account.Snapshot! with { ResetCredits = [new("A", "Same", null, Now.AddHours(1)), new("B", "Same", null, Now.AddHours(1))] } };
        state = state with { Accounts = [account, account with { Profile = new(Guid.NewGuid(), "other@example.test") }] };
        var due = ReminderPlanner.Due(state, [new(ResetKind.Reserve, true, [60], [ReminderChannel.Windows], [AccountId])], Now);
        Assert.Equal(2, due.Count); Assert.Equal(2, due.Select(d => d.Key).Distinct().Count()); Assert.All(due, d => Assert.Equal(AccountId, d.AccountId));
    }
    [Fact]
    public void ChannelsAreIndependentForEachLeadAndShortLeadsAreRestricted()
    {
        var rules = new ReminderRule[] { new(ResetKind.Weekly, true, [1440], [ReminderChannel.Windows]), new(ResetKind.Weekly, true, [60], [ReminderChannel.Sms]), new(ResetKind.Short, true, [30, 60, 360, 1440], [ReminderChannel.Email]) };
        var normalized = ReminderPlanner.Normalize(rules);
        Assert.Equal(4, normalized.Length); Assert.DoesNotContain(normalized, r => r.Kind == ResetKind.Short && r.LeadMinutes[0] >= 300);
        var due = ReminderPlanner.Due(State(), rules, Now); Assert.Equal(3, due.Count);
        Assert.Contains(due, d => d.Kind == ResetKind.Weekly && d.LeadMinutes == 60 && d.Channel == ReminderChannel.Sms);
    }
    [Theory]
    [InlineData("2026-10-25T00:30:00Z", true)] [InlineData("2026-10-25T01:30:00Z", true)]
    [InlineData("2026-10-25T07:00:00Z", false)] [InlineData("2026-03-29T01:30:00Z", true)] [InlineData("2026-03-29T06:00:00Z", false)]
    public void QuietHoursRespectParisClockChanges(string at, bool quiet) => Assert.Equal(quiet, ReminderPlanner.IsQuiet(DateTimeOffset.Parse(at), new()));
    [Fact]
    public void LegacyPreferencesKeepDisabledExpiryAndCustomHorizon()
    {
        using var dir = new TestDirectory(); File.WriteAllText(dir.File("preferences.json"), "{\"expiryNotifications\":false,\"expiryLeadHours\":72}");
        var prefs = new PreferencesStore(dataDirectory: dir.Root);
        Assert.All(prefs.Current.ReminderRules!.Where(r => r.Kind == ResetKind.Reserve), r => Assert.False(r.Enabled));
        Assert.Contains(prefs.Current.ReminderRules!, r => r.Kind == ResetKind.Reserve && r.LeadMinutes.Contains(4320));
        Assert.All(prefs.Current.ReminderRules!, r => Assert.Equal([ReminderChannel.Windows], r.Channels));
    }
    [Fact]
    public void SecretsAreEncryptedAndSurviveRestartWithoutPlaintext()
    {
        using var dir = new TestDirectory(); new NotificationSecretStore(dir.Root).Save(Config);
        var bytes = File.ReadAllBytes(dir.File("notification-secrets.dpapi"));
        Assert.DoesNotContain("fake-secret", Encoding.UTF8.GetString(bytes)); Assert.DoesNotContain("fake-sendgrid-key", Encoding.UTF8.GetString(bytes));
        Assert.Equal(Config, new NotificationSecretStore(dir.Root).Read());
    }
    [Fact]
    public async Task CatchupGroupsDesktopAndDoesNotRepeatAfterRestart()
    {
        using var fixture = new Fixture(); await fixture.Dispatcher.TickAsync();
        Assert.Single(fixture.Desktop); Assert.Single(fixture.Desktop[0]); Assert.Equal(60, fixture.Desktop[0][0].LeadMinutes);
        Assert.Contains(fixture.Journal.Entries, d => d.Status == DeliveryStatus.Skipped && d.Occurrence.LeadMinutes == 1440);
        await fixture.Dispatcher.TickAsync(); fixture.Recreate(); await fixture.Dispatcher.TickAsync(); Assert.Single(fixture.Desktop);
        Assert.Equal(0, fixture.Handler.Requests);
    }
    [Theory]
    [InlineData(DeliveryStatus.Accepted)] [InlineData(DeliveryStatus.Skipped)] [InlineData(DeliveryStatus.Failed)]
    public async Task GroupedWindowsRemindersPersistAdapterResultWithoutRetry(DeliveryStatus status)
    {
        using var f = new Fixture();
        f.Rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Windows]), new(ResetKind.Short, true, [60], [ReminderChannel.Windows])];
        f.WindowsResult = new(status, "Résultat fourni par Windows.");
        await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(2, f.Desktop[0].Count); Assert.True(f.PersistedBeforeWindows);
        Assert.Equal(2, f.Journal.Entries.Count);
        Assert.All(f.Journal.Entries, d => { Assert.Equal(status, d.Status); Assert.Equal(f.WindowsResult.Detail, d.Detail); });
        f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.All(f.Journal.Entries, d => Assert.Equal(status, d.Status));
        Assert.Equal(0, f.Handler.Requests);
    }
    [Theory]
    [InlineData(DeliveryStatus.Accepted)] [InlineData(DeliveryStatus.Skipped)] [InlineData(DeliveryStatus.Failed)]
    public async Task WindowsTestReturnsAndPersistsAdapterResult(DeliveryStatus status)
    {
        using var f = new Fixture(); f.Rules = [];
        f.WindowsResult = new(status, "Résultat du test Windows.");
        var result = await f.Dispatcher.TestAsync(ReminderChannel.Windows);
        Assert.Equal(f.WindowsResult, result); Assert.Single(f.Desktop); Assert.True(f.PersistedBeforeWindows);
        var entry = Assert.Single(f.Journal.Entries);
        Assert.True(entry.IsTest); Assert.Equal(status, entry.Status); Assert.Equal(result.Detail, entry.Detail);
        Assert.Equal(Now, entry.AttemptedAt);
        f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(status, Assert.Single(f.Journal.Entries).Status);
        Assert.Equal(0, f.Handler.Requests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task WindowsAdapterExceptionPersistsSanitizedFailureWithoutRetry(bool manualTest)
    {
        using var f = new Fixture(); f.WindowsThrows = true;
        f.Rules = manualTest ? [] : [new(ResetKind.Weekly, true, [60], [ReminderChannel.Windows])];
        if (manualTest)
        {
            var result = await f.Dispatcher.TestAsync(ReminderChannel.Windows);
            Assert.Equal(DeliveryStatus.Failed, result.Status); Assert.DoesNotContain("fake-secret", result.Detail);
        }
        else await f.Dispatcher.TickAsync();
        var entry = Assert.Single(f.Journal.Entries);
        Assert.Equal(DeliveryStatus.Failed, entry.Status); Assert.DoesNotContain("fake-secret", entry.Detail); Assert.True(f.PersistedBeforeWindows);
        Assert.Contains("Windows", entry.Detail); Assert.Equal(manualTest, entry.IsTest);
        f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(DeliveryStatus.Failed, Assert.Single(f.Journal.Entries).Status);
        Assert.Equal(0, f.Handler.Requests);
    }
    [Fact]
    public async Task DeferredWindowsReminderRetriesAfterShellRecoversAndDoesNotRepeat()
    {
        using var f = new Fixture(); f.Rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Windows])];
        f.WindowsResult = new(DeliveryStatus.Deferred, "Windows est temporairement occupé.");
        await f.Dispatcher.TickAsync();
        Assert.Equal(DeliveryStatus.Deferred, Assert.Single(f.Journal.Entries).Status); Assert.Single(f.Desktop);
        f.Recreate(); f.Clock.Now = Now.AddMinutes(1); f.WindowsResult = new(DeliveryStatus.Accepted, "Transmis à Windows.");
        await f.Dispatcher.TickAsync();
        Assert.Equal(2, f.Desktop.Count); Assert.Equal(DeliveryStatus.Accepted, Assert.Single(f.Journal.Entries).Status);
        await f.Dispatcher.TickAsync(); f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Equal(2, f.Desktop.Count); Assert.Equal(0, f.Handler.Requests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DeferredWindowsReminderNeverSendsAfterExpiryOrEventRemoval(bool removed)
    {
        using var f = new Fixture(); f.Rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Windows])];
        f.WindowsResult = new(DeliveryStatus.Deferred, "Windows est temporairement occupé.");
        await f.Dispatcher.TickAsync();
        Assert.Equal(DeliveryStatus.Deferred, Assert.Single(f.Journal.Entries).Status);
        if (removed) f.State = new([], null); else f.Clock.Now = Now.AddHours(1);
        f.WindowsResult = new(DeliveryStatus.Accepted, "Transmis à Windows.");
        await f.Dispatcher.TickAsync(); f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(DeliveryStatus.Cancelled, Assert.Single(f.Journal.Entries).Status);
        Assert.Equal(0, f.Handler.Requests);
    }
    [Fact]
    public async Task EachOffsetSendsOnceWhenTrackerRemainsOpen()
    {
        using var fixture = new Fixture(); fixture.State = State(Now.AddHours(20)); await fixture.Dispatcher.TickAsync();
        fixture.Clock.Now = Now.AddHours(19); await fixture.Dispatcher.TickAsync();
        Assert.Equal(2, fixture.Desktop.Count); Assert.Equal(1440, fixture.Desktop[0][0].LeadMinutes); Assert.Equal(60, fixture.Desktop[1][0].LeadMinutes);
    }
    [Fact]
    public async Task OldExpiryReceiptSuppressesMigratedDuplicate()
    {
        using var f = new Fixture(); f.Rules = Rule(kind: ResetKind.Reserve);
        f.Legacy[CalendarExport.Identity(AccountId, "credit", "credit-A", Now.AddHours(1))] = Now.AddHours(1);
        await f.Dispatcher.TickAsync(); Assert.Empty(f.Desktop); Assert.All(f.Journal.Entries, d => Assert.Equal(DeliveryStatus.Skipped, d.Status));
    }
    [Fact]
    public async Task NetworkUncertaintyAndRestartNeverRepeatSms()
    {
        using var f = new Fixture(); f.Rules = Rule(ReminderChannel.Sms); f.Handler.Fail = true;
        await f.Dispatcher.TickAsync(); Assert.Equal(1, f.Handler.Requests);
        Assert.Contains(f.Journal.Entries, d => d.Status == DeliveryStatus.Unknown);
        f.Recreate(); await f.Dispatcher.TickAsync(); Assert.Equal(1, f.Handler.Requests);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task DeferredReminderIsCancelledWhenEventOrRuleDisappears(int change)
    {
        using var f = new Fixture(); f.Rules = Rule(ReminderChannel.Sms); f.Policy = new(SmsPerDay: 0);
        await f.Dispatcher.TickAsync(); Assert.Contains(f.Journal.Entries, d => d.Status == DeliveryStatus.Deferred);
        switch (change) {
            case 0: f.State = new([], null); break;
            case 1: f.Rules = []; break;
            case 2: f.State = State(Now.AddDays(2)); break;
            default: f.Clock.Now = Now.AddHours(2); break;
        }
        await f.Dispatcher.TickAsync(); Assert.DoesNotContain(f.Journal.Entries, d => d.Status == DeliveryStatus.Deferred); Assert.Equal(0, f.Handler.Requests);
    }
    [Fact]
    public async Task QuietDeferralSendsOnlyIfStillFutureAndTestRespectsLimits()
    {
        using var f = new Fixture(); f.Rules = Rule(ReminderChannel.Sms); f.Clock.Now = DateTimeOffset.Parse("2026-09-21T21:00:00Z"); f.State = State(f.Clock.Now.AddHours(12));
        await f.Dispatcher.TickAsync(); Assert.Equal(0, f.Handler.Requests);
        Assert.Equal(DeliveryStatus.Skipped, (await f.Dispatcher.TestAsync(ReminderChannel.Call)).Status);
        f.Clock.Now = DateTimeOffset.Parse("2026-09-22T06:00:00Z"); await f.Dispatcher.TickAsync(); Assert.Equal(1, f.Handler.Posts);
    }
    [Fact]
    public void DailyLimitsUseLocalDateAndCountUncertainAttempts()
    {
        var at = DateTimeOffset.Parse("2026-09-21T22:30:00Z"); var r = Occurrence(ReminderChannel.Sms);
        var history = new[] { new ReminderDelivery(r, DeliveryStatus.Unknown, at, "", AttemptedAt: at) };
        Assert.True(ReminderPlanner.AtDailyLimit(ReminderChannel.Sms, at.AddMinutes(30), new(SmsPerDay: 1), history));
        Assert.False(ReminderPlanner.AtDailyLimit(ReminderChannel.Sms, at.AddDays(1), new(SmsPerDay: 1), history));
    }
    [Fact]
    public void JournalRecoversInterruptedAttemptAndFailsClosedOnCorruption()
    {
        using var dir = new TestDirectory(); var journal = new ReminderJournal(dir.Root);
        journal.Put(new(Occurrence(ReminderChannel.Sms), DeliveryStatus.Submitting, Now, "", AttemptedAt: Now));
        Assert.Equal(DeliveryStatus.Unknown, new ReminderJournal(dir.Root).Entries.Single().Status);
        File.WriteAllText(dir.File("reminder-journal.json"), "broken"); Assert.Throws<JsonException>(() => new ReminderJournal(dir.Root));
        Assert.Equal("broken", File.ReadAllText(dir.File("reminder-journal.json")));
    }
    [Fact]
    public async Task ConcurrentTicksSerializeOneSendAndPollDelivered()
    {
        using var f = new Fixture(); f.Rules = Rule(ReminderChannel.Sms); f.Handler.Delay = true;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Dispatcher.TickAsync()));
        Assert.Equal(1, f.Handler.Posts); Assert.Contains(f.Journal.Entries, d => d.Status == DeliveryStatus.Delivered);
    }
    [Fact]
    public async Task SuspendDuringHttpMarksUncertainAndStopsFollowingChannels()
    {
        using var f = new Fixture(); f.Handler.Delay = true;
        f.Rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Sms, ReminderChannel.Call])];
        using var cancellation = new CancellationTokenSource();
        f.Handler.Started = () => cancellation.Cancel();
        await f.Dispatcher.TickAsync(cancellation.Token);
        Assert.Equal(1, f.Handler.Posts); Assert.Single(f.Journal.Entries); Assert.Equal(DeliveryStatus.Unknown, f.Journal.Entries[0].Status);
    }
    [Fact]
    public async Task DeletingConnectorCancelsDeferredAndKeepsWindowsWorking()
    {
        using var f = new Fixture(); f.Rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Sms, ReminderChannel.Windows])]; f.Policy = new(SmsPerDay: 0);
        await f.Dispatcher.TickAsync(); f.Secrets.Save(NotificationSecrets.Empty); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Contains(f.Journal.Entries, d => d.Occurrence.Channel == ReminderChannel.Sms && d.Status == DeliveryStatus.Cancelled); Assert.Equal(0, f.Handler.Posts);
    }
    [Theory] [InlineData(ReminderChannel.Sms)] [InlineData(ReminderChannel.Call)] [InlineData(ReminderChannel.Email)]
    public async Task ProvidersSendExpectedPayloadWithoutWebhook(ReminderChannel channel)
    {
        using var handler = new Handler(); using var client = new HttpClient(handler); var providers = new NotificationProviders(client);
        var result = await providers.SendAsync(Occurrence(channel), Config, default);
        Assert.Equal(DeliveryStatus.Accepted, result.Status); Assert.Equal("https", handler.Uri!.Scheme);
        Assert.DoesNotContain("StatusCallback", handler.Body!);
        if (channel == ReminderChannel.Email) { Assert.Equal("Bearer", handler.Auth); Assert.Contains("studio@example.test", handler.Body!); }
        else
        {
            Assert.Equal("Basic", handler.Auth);
            var form = handler.Body!.Split('&').Select(v => v.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            if (channel == ReminderChannel.Sms) { Assert.InRange(form["Body"].Length, 1, 160); Assert.All(form["Body"], c => Assert.InRange((int)c, 32, 126)); }
            else { Assert.Contains("<Say language=\"fr-FR\">", form["Twiml"]); Assert.Equal("60", form["TimeLimit"]); Assert.DoesNotContain("Url", form.Keys); }
        }
    }
    [Theory] [InlineData(401, DeliveryStatus.Failed)] [InlineData(429, DeliveryStatus.Failed)] [InlineData(500, DeliveryStatus.Unknown)]
    public async Task ProviderErrorsAreSanitizedAndNotRetried(int status, DeliveryStatus expected)
    {
        using var handler = new Handler { Status = status }; using var http = new HttpClient(handler);
        var result = await new NotificationProviders(http).SendAsync(Occurrence(ReminderChannel.Email), Config, default);
        Assert.Equal(expected, result.Status); Assert.DoesNotContain("fake-secret", result.Detail); Assert.Equal(1, handler.Posts);
    }
    [Fact]
    public void PruningPreservesFutureDedupBeyondThirtyDays()
    {
        using var dir = new TestDirectory(); var journal = new ReminderJournal(dir.Root);
        journal.Put(new(Occurrence(ReminderChannel.Windows) with { At = Now.AddDays(1) }, DeliveryStatus.Skipped, Now.AddDays(-31), ""));
        journal.Put(new(Occurrence(ReminderChannel.Windows) with { Key = "old", At = Now.AddDays(-31) }, DeliveryStatus.Shown, Now.AddDays(-31), ""));
        journal.Prune(Now); Assert.Single(journal.Entries);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = ReminderTests.Now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests, Posts; public bool Fail, Delay; public Action? Started; public int Status = 200; public string? Body, Auth; public Uri? Uri;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; if (request.Method == HttpMethod.Post) Posts++;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token); Uri = request.RequestUri; Auth = request.Headers.Authorization?.Scheme;
            Started?.Invoke(); if (Delay) await Task.Delay(20, token);
            if (Fail) throw new HttpRequestException("fake-secret must never be logged");
            var sid = (request.RequestUri!.AbsolutePath.Contains("Calls") ? "CA" : "SM") + new string('c', 32);
            return new((HttpStatusCode)Status) { Content = new StringContent(Status == 200 ? JsonSerializer.Serialize(new { sid, status = "delivered" }) : "fake-secret") };
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory _directory = new();
        public readonly Clock Clock = new(); public readonly Handler Handler = new(); private readonly HttpClient _http;
        public TrackerState State = ReminderTests.State(); public ReminderRule[] Rules = Rule(); public PhonePolicy Policy = new();
        public readonly Dictionary<string, DateTimeOffset> Legacy = []; public readonly List<IReadOnlyList<ReminderOccurrence>> Desktop = [];
        public DeliveryResult WindowsResult = new(DeliveryStatus.Accepted, "Transmis à Windows ; affichage non confirmé."); public bool WindowsThrows, PersistedBeforeWindows;
        public NotificationSecretStore Secrets; public ReminderJournal Journal = null!; public ReminderDispatcher Dispatcher = null!;
        public Fixture() { _http = new(Handler); Secrets = new(_directory.Root); Secrets.Save(Config); Recreate(); }
        public void Recreate() { Journal = new(_directory.Root); Dispatcher = new(Journal, Secrets, new(_http), () => State, () => Rules, () => Policy, () => Legacy, ShowWindows, Clock); }
        private DeliveryResult ShowWindows(IReadOnlyList<ReminderOccurrence> rows)
        {
            Desktop.Add(rows);
            PersistedBeforeWindows = rows.All(r => Journal.Entries.Any(d => d.Occurrence.Key == r.Key && d.Status == DeliveryStatus.Submitting));
            if (WindowsThrows) throw new InvalidOperationException("fake-secret must never be logged");
            return WindowsResult;
        }
        public void Dispose() { _http.Dispose(); _directory.Dispose(); }
    }
}
