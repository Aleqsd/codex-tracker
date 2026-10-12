using CodexTracker.App;
using CodexTracker.Codex;
using CodexTracker.Core;
using System.Text.Json;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ResetAlertTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T10:00:00Z");
    private static readonly DateTimeOffset At = Now.AddHours(1);
    private static readonly Guid AccountId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static ResetAlert Alert(ResetKind kind = ResetKind.Short, bool estimated = false) =>
        new(AccountId, kind, At, Now, Now.AddMinutes(-10), estimated);
    private static TrackerState State(DateTimeOffset? at = null, ResetKind kind = ResetKind.Short,
        AccountProvider provider = AccountProvider.ClaudeCode, bool active = true, DateTimeOffset? observed = null, bool estimated = false)
    {
        var date = at ?? At;
        var shortWindow = new QuotaWindow(100, 300, kind == ResetKind.Short ? date : Now.AddHours(2));
        var weeklyWindow = new QuotaWindow(70, 10080, kind == ResetKind.Weekly ? date : Now.AddDays(3),
            estimated ? date.AddMinutes(-1) : null);
        return new([new(new(AccountId, "studio@example.test", provider),
            new("studio@example.test", "team", [new(provider == AccountProvider.ClaudeCode ? "claude" : "codex", null, [shortWindow, weeklyWindow])],
                null, null, observed ?? Now.AddMinutes(-10)),
            IsActiveInCodex: active && provider == AccountProvider.Codex,
            IsActiveInClaudeCode: active && provider == AccountProvider.ClaudeCode)], AccountId);
    }

    [Theory]
    [InlineData(-1, 0)] [InlineData(0, 1)] [InlineData(86399, 1)] [InlineData(86400, 0)]
    public void SendsAtTheDeadlineWithBoundedCatchup(int seconds, int count) =>
        Assert.Equal(count, ResetAlertPlanner.Due(State(), [Alert()], At.AddSeconds(seconds)).Count);

    [Theory]
    [InlineData(ResetKind.Short, AccountProvider.ClaudeCode)] [InlineData(ResetKind.Weekly, AccountProvider.ClaudeCode)]
    [InlineData(ResetKind.Short, AccountProvider.Codex)] [InlineData(ResetKind.Weekly, AccountProvider.Codex)]
    public void AlertNamesTheExactAccountProviderAndWindow(ResetKind kind, AccountProvider provider)
    {
        var occurrence = Assert.Single(ResetAlertPlanner.Due(State(kind: kind, provider: provider), [Alert(kind)], At));
        Assert.Equal(AccountId, occurrence.AccountId); Assert.Equal(kind, occurrence.Kind); Assert.Equal(provider, occurrence.Provider);
        Assert.Equal(ReminderChannel.Windows, occurrence.Channel); Assert.Equal(0, occurrence.LeadMinutes);
        Assert.Contains(occurrence.ProviderName, ReminderPlanner.Body(occurrence));
        Assert.Contains(kind == ResetKind.Short ? "5 heures" : "hebdomadaire", ReminderPlanner.Body(occurrence));
        Assert.Contains(At.ToLocalTime().ToString("HH:mm"), ReminderPlanner.Body(occurrence));
        Assert.Contains("confirmer", ReminderPlanner.Body(occurrence));
        Assert.DoesNotContain("100 %", ReminderPlanner.Body(occurrence));
    }

    [Fact]
    public void WeeklyEstimateIsExplicitAndNeverConfirmsARefill()
    {
        var occurrence = Assert.Single(ResetAlertPlanner.Due(State(kind: ResetKind.Weekly, estimated: true), [Alert(ResetKind.Weekly, true)], At));
        Assert.Contains("estimée", ReminderPlanner.Title(occurrence));
        Assert.Contains("estimé", ReminderPlanner.Body(occurrence)); Assert.Contains("confirmer", ReminderPlanner.Body(occurrence));
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void InvalidDatesKindsAndIdentitiesCannotEnterTheQueue(int invalid)
    {
        var alert = invalid switch
        {
            0 => Alert() with { AccountId = Guid.Empty }, 1 => Alert() with { Kind = ResetKind.Reserve },
            2 => Alert() with { At = Now }, 3 => Alert() with { At = Now.AddHours(6) },
            4 => Alert() with { ObservedAt = Now.AddSeconds(1) }, _ => Alert() with { IsEstimated = true }
        };
        Assert.False(ResetAlertPlanner.Valid(alert)); Assert.Empty(ResetAlertPlanner.Due(State(), [alert], At));
    }

    [Fact]
    public void EquivalentOffsetsShareOneIdentityAndLatestChoiceWins()
    {
        var alert = Alert(); var disabled = alert with { At = At.ToOffset(TimeSpan.FromHours(2)), Enabled = false };
        Assert.Equal(disabled, Assert.Single(ResetAlertPlanner.Normalize([alert, disabled])));
        Assert.Empty(ResetAlertPlanner.Due(State(), [alert, disabled], At));
        Assert.True(ResetAlertPlanner.Matches(disabled, AccountId, ResetKind.Short, At));
        Assert.Equal(ResetAlertPlanner.MaximumAlerts, ResetAlertPlanner.Normalize(Enumerable.Range(1, 140)
            .Select(_ => alert with { AccountId = Guid.NewGuid() })).Length);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void ActiveAndInactiveAccountsCanOptIn(bool active) =>
        Assert.Single(ResetAlertPlanner.Due(State(active: active), [Alert()], At));

    [Fact]
    public void DisabledDeletedOrFutureObservedAccountsDoNotNotify()
    {
        Assert.Empty(ResetAlertPlanner.Due(State(), [Alert() with { Enabled = false }], At));
        Assert.Empty(ResetAlertPlanner.Due(new([], null), [Alert()], At));
        Assert.Empty(ResetAlertPlanner.Due(State(observed: At.AddSeconds(1)), [Alert()], At));
    }

    [Fact]
    public void ANewDeadlineBeforeTheRequestedTimeCancelsTheObsoleteAlert()
    {
        Assert.Empty(ResetAlertPlanner.Due(State(At.AddHours(1), observed: At.AddMinutes(-1)), [Alert()], At));
        // A reading from the following period cannot erase a notification requested for the period just ended.
        Assert.Single(ResetAlertPlanner.Due(State(At.AddHours(1), observed: At.AddSeconds(1)), [Alert()], At.AddSeconds(2)));
    }

    [Fact]
    public void DeclaringAnEarlierCodexResetAfterOptingInCancelsTheOldAppointment()
    {
        var alert = Alert() with { CreatedAt = Now.AddMinutes(-30), ObservedAt = Now.AddHours(-2) };
        var state = State(provider: AccountProvider.Codex, observed: Now.AddHours(-2)) with
            { ManualCodexReset = new(Now.AddHours(-1), Now.AddMinutes(-1)) };
        Assert.Empty(ResetAlertPlanner.Due(state, [alert], At));
        // A declaration for Codex does not affect Claude.
        Assert.Single(ResetAlertPlanner.Due(State() with { ManualCodexReset = state.ManualCodexReset }, [Alert()], At));
    }

    [Theory] [InlineData("{}")] [InlineData("{\"resetAlerts\":null}")]
    public void OldPreferencesNeedNoRewriteAndCreateNoAlert(string content)
    {
        using var dir = new TestDirectory(); File.WriteAllText(dir.File("preferences.json"), content);
        Assert.Empty(new PreferencesStore(dataDirectory: dir.Root).Current.ResetAlerts);
        Assert.Equal(content, File.ReadAllText(dir.File("preferences.json")));
    }

    [Fact]
    public void OptInAndCancellationSurviveRestartAndDoNotChangeRecurringRules()
    {
        using var dir = new TestDirectory(); var preferences = new PreferencesStore(dataDirectory: dir.Root);
        var commands = new ApplicationCommands(preferences, new(dir.Root)); var rules = preferences.Current.ReminderRules!.ToArray();
        commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, true, Now, commands.Revision);
        Assert.Equal(Alert(), Assert.Single(new PreferencesStore(dataDirectory: dir.Root).Current.ResetAlerts));
        Assert.Equal(JsonSerializer.Serialize(rules), JsonSerializer.Serialize(preferences.Current.ReminderRules));
        var saved = File.ReadAllBytes(dir.File("preferences.json")); var revision = commands.Revision;
        commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, true, Now.AddSeconds(1), revision);
        Assert.Equal(revision, commands.Revision); Assert.Equal(saved, File.ReadAllBytes(dir.File("preferences.json")));
        commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, false, Now, revision);
        Assert.Empty(new PreferencesStore(dataDirectory: dir.Root).Current.ResetAlerts);
        var cancelledRevision = commands.Revision;
        commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, false, Now, cancelledRevision);
        Assert.Equal(cancelledRevision, commands.Revision);
    }

    [Fact]
    public void StaleConcurrentCommandsCannotOverwriteTheFirstChoice()
    {
        using var dir = new TestDirectory(); var preferences = new PreferencesStore(dataDirectory: dir.Root);
        var commands = new ApplicationCommands(preferences, new(dir.Root)); var revision = commands.Revision;
        commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, true, Now, revision);
        var error = Assert.Throws<InvalidOperationException>(() => commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, false, Now, revision));
        Assert.Equal("revision_conflict", error.Message); Assert.Single(preferences.Current.ResetAlerts);
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CommandRejectsUnknownPastChangedFutureOrUnsupportedReset(int invalid)
    {
        using var dir = new TestDirectory(); var preferences = new PreferencesStore(dataDirectory: dir.Root);
        var commands = new ApplicationCommands(preferences, new(dir.Root)); var state = State();
        var at = At; var kind = ResetKind.Short;
        switch (invalid)
        {
            case 0: state = new([], null); break;
            case 1: state = new([state.Accounts[0] with { Snapshot = null }], AccountId); break;
            case 2: at = Now.AddSeconds(-1); break;
            case 3: at = At.AddSeconds(1); break;
            case 4: state = State(observed: Now.AddSeconds(1)); break;
            default: kind = ResetKind.Reserve; break;
        }
        Assert.Throws<ArgumentException>(() => commands.SetResetAlert(state, AccountId, kind, at, true, Now, commands.Revision));
        Assert.Empty(preferences.Current.ResetAlerts); Assert.False(File.Exists(dir.File("preferences.json")));
    }

    [Fact]
    public void ProAccountCannotScheduleANonexistentShortWindow()
    {
        using var dir = new TestDirectory(); var preferences = new PreferencesStore(false, dir.Root);
        var commands = new ApplicationCommands(preferences, new(dir.Root)); var state = State(provider: AccountProvider.Codex);
        state = state with { Accounts = [state.Accounts[0] with { Snapshot = state.Accounts[0].Snapshot! with { PlanType = "pro" } }] };
        Assert.Throws<ArgumentException>(() => commands.SetResetAlert(state, AccountId, ResetKind.Short, At, true, Now, commands.Revision));
        Assert.Empty(ResetAlertPlanner.Due(state, [Alert()], At));
    }

    [Fact]
    public void RecoveryDisablesSavedAlertsAndRequiresANewExplicitChoice()
    {
        using var dir = new TestDirectory(); var preferences = new PreferencesStore(dataDirectory: dir.Root);
        preferences.Update(p => p with { ResetAlerts = [Alert()] }); File.WriteAllText(dir.File("preferences.json"), "{broken");
        var recovered = new PreferencesStore(dataDirectory: dir.Root); var commands = new ApplicationCommands(recovered, new(dir.Root));
        Assert.True(recovered.RecoveryRequired); Assert.False(Assert.Single(recovered.Current.ResetAlerts).Enabled);
        Assert.Throws<InvalidOperationException>(() => commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, true, Now, commands.Revision));
        recovered.AcknowledgeRecovery(); Assert.False(Assert.Single(recovered.Current.ResetAlerts).Enabled);
        commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, true, Now, commands.Revision);
        Assert.True(Assert.Single(new PreferencesStore(dataDirectory: dir.Root).Current.ResetAlerts).Enabled);
    }

    [Fact]
    public void ProfileImportKeepsLocalAppointments()
    {
        var local = new TrackerPreferences { ResetAlerts = [Alert()] };
        var imported = new TrackerPreferences { ResetAlerts = [Alert(ResetKind.Weekly)] };
        var merged = ProfileTransfer.Apply(local, imported, new Dictionary<Guid, Guid> { [AccountId] = AccountId }, new Dictionary<string, string>());
        Assert.Equal(local.ResetAlerts, merged.ResetAlerts);
    }

    [Fact]
    public void CommandKeepsTheWeeklyEstimateAndRejectsAnUnboundedQueue()
    {
        using var dir = new TestDirectory(); var preferences = new PreferencesStore(dataDirectory: dir.Root);
        var commands = new ApplicationCommands(preferences, new(dir.Root));
        commands.SetResetAlert(State(kind: ResetKind.Weekly, estimated: true), AccountId, ResetKind.Weekly, At, true, Now, commands.Revision);
        Assert.True(Assert.Single(new PreferencesStore(dataDirectory: dir.Root).Current.ResetAlerts).IsEstimated);
        var bounded = Enumerable.Range(0, ResetAlertPlanner.MaximumAlerts).Select(_ => Alert() with { AccountId = Guid.NewGuid() }).ToArray();
        commands.SavePreferences(p => p with { ResetAlerts = bounded });
        Assert.Throws<ArgumentException>(() => commands.SetResetAlert(State(), AccountId, ResetKind.Short, At, true, Now, commands.Revision));
        Assert.Throws<ArgumentException>(() => commands.SavePreferences(p => p with { ResetAlerts = [Alert() with { Kind = ResetKind.Reserve }] }));
        Assert.Equal(bounded.Length, preferences.Current.ResetAlerts.Length);
    }

    [Fact]
    public async Task ConcurrentTicksAndRestartsDeliverOneAlertWithoutHttp()
    {
        using var f = new Fixture(); await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Dispatcher.TickAsync())));
        Assert.Single(f.Desktop); Assert.True(f.PersistedBeforeWindows);
        Assert.Equal(DeliveryStatus.Accepted, Assert.Single(f.Journal.Entries).Status);
        f.Recreate(); await f.Dispatcher.TickAsync(); Assert.Single(f.Desktop); Assert.Equal(0, f.Handler.Requests);
    }

    [Fact]
    public async Task RestartRestoresTheSavedChoiceAndJournalSuppressesRepeatedDelivery()
    {
        using var f = new Fixture(); var preferences = new PreferencesStore(dataDirectory: f.Directory.Root);
        var commands = new ApplicationCommands(preferences, f.Secrets);
        commands.SetResetAlert(f.State, AccountId, ResetKind.Short, At, true, Now, commands.Revision);
        f.Alerts = new PreferencesStore(dataDirectory: f.Directory.Root).Current.ResetAlerts; f.Recreate();
        await f.Dispatcher.TickAsync(); f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.True(ReminderPlanner.IsRequestedReset(Assert.Single(f.Desktop[0])));
    }

    [Fact]
    public async Task DeferredWindowsDeliveryIsCancelledAfterOptingOut()
    {
        using var f = new Fixture(); f.WindowsResult = new(DeliveryStatus.Deferred, "Shell temporairement indisponible.");
        await f.Dispatcher.TickAsync(); Assert.Equal(DeliveryStatus.Deferred, Assert.Single(f.Journal.Entries).Status);
        f.Alerts = []; f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(DeliveryStatus.Cancelled, Assert.Single(f.Journal.Entries).Status);
        Assert.Equal(0, f.Handler.Requests);
    }

    [Fact]
    public async Task DeferredWindowsDeliveryRetriesOnlyWhileTheAppointmentIsValid()
    {
        using var f = new Fixture(); f.WindowsResult = new(DeliveryStatus.Deferred, "Shell temporairement indisponible.");
        await f.Dispatcher.TickAsync(); f.Recreate(); f.Clock.Now = At.AddMinutes(1);
        f.WindowsResult = new(DeliveryStatus.Accepted, "Transmis à Windows."); await f.Dispatcher.TickAsync();
        Assert.Equal(2, f.Desktop.Count); f.Recreate(); await f.Dispatcher.TickAsync(); Assert.Equal(2, f.Desktop.Count);
        Assert.Equal(DeliveryStatus.Accepted, Assert.Single(f.Journal.Entries).Status); Assert.Equal(0, f.Handler.Requests);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DeferredDeliveryExpiresOrCancelsWhenTheNativeDeadlineChanged(bool changed)
    {
        using var f = new Fixture(); f.WindowsResult = new(DeliveryStatus.Deferred, "Shell temporairement indisponible.");
        await f.Dispatcher.TickAsync();
        if (changed) f.State = State(At.AddHours(1), observed: At.AddMinutes(-1)); else f.Clock.Now = At.AddDays(1);
        f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(DeliveryStatus.Cancelled, Assert.Single(f.Journal.Entries).Status);
    }

    [Theory] [InlineData(DeliveryStatus.Failed)] [InlineData(DeliveryStatus.Unknown)]
    public async Task ARecordedFailureOrUncertainDeliveryIsNotRetried(DeliveryStatus status)
    {
        using var f = new Fixture(); f.WindowsResult = new(status, "Résultat simulé.");
        await f.Dispatcher.TickAsync(); f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(status, Assert.Single(f.Journal.Entries).Status);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RequestedAndAutomaticNotificationsForTheSameResetCannotDuplicate(bool automaticFirst)
    {
        using var f = new Fixture(); f.State = State(active: false); f.ExpectedResets = true;
        if (automaticFirst) f.Alerts = [];
        await f.Dispatcher.TickAsync(); Assert.Single(f.Desktop); Assert.Single(f.Desktop[0]);
        Assert.Equal(!automaticFirst, ReminderPlanner.IsRequestedReset(f.Desktop[0][0]));
        f.Alerts = automaticFirst ? [Alert()] : []; f.Recreate(); await f.Dispatcher.TickAsync();
        Assert.Single(f.Desktop); Assert.Equal(0, f.Handler.Requests);
    }

    [Fact]
    public async Task SeparateWindowsAreGroupedWithoutConflatingTheirResetTimes()
    {
        using var f = new Fixture(); f.State = State(kind: ResetKind.Weekly);
        // Both windows end at the same time, but each keeps its own identity and message.
        f.State = f.State with { Accounts = [f.State.Accounts[0] with { Snapshot = f.State.Accounts[0].Snapshot! with
            { Buckets = [new("claude", null, [new(100, 300, At), new(70, 10080, At)])] } }] };
        f.Alerts = [Alert(), Alert(ResetKind.Weekly)]; await f.Dispatcher.TickAsync();
        Assert.Equal(2, Assert.Single(f.Desktop).Count); Assert.Equal(2, f.Journal.Entries.Count);
        f.Recreate(); await f.Dispatcher.TickAsync(); Assert.Single(f.Desktop); Assert.Equal(0, f.Handler.Requests);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = At;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("A one-off Windows alert must not make an HTTP request."); }
    }
    private sealed class Fixture : IDisposable
    {
        public readonly TestDirectory Directory = new(); public readonly Clock Clock = new(); public readonly Handler Handler = new();
        private readonly HttpClient _http; public readonly NotificationSecretStore Secrets;
        public TrackerState State = ResetAlertTests.State(); public ResetAlert[] Alerts = [Alert()]; public bool ExpectedResets;
        public readonly List<IReadOnlyList<ReminderOccurrence>> Desktop = [];
        public DeliveryResult WindowsResult = new(DeliveryStatus.Accepted, "Transmission simulée."); public bool PersistedBeforeWindows;
        public ReminderJournal Journal = null!; public ReminderDispatcher Dispatcher = null!;
        public Fixture() { _http = new(Handler); Secrets = new(Directory.Root); Recreate(); }
        public void Recreate()
        {
            Journal = new(Directory.Root);
            Dispatcher = new(Journal, Secrets, new(_http), () => State, () => [], () => new(), () => new Dictionary<string, DateTimeOffset>(), rows =>
            {
                PersistedBeforeWindows = rows.All(r => Journal.Entries.Any(d => d.Occurrence.Key == r.Key && d.Status == DeliveryStatus.Submitting));
                Desktop.Add(rows); return WindowsResult;
            }, Clock, () => ExpectedResets, () => Alerts);
        }
        public void Dispose() { _http.Dispose(); Directory.Dispose(); }
    }
}
