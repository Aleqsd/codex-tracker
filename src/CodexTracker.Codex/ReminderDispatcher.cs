using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed class ReminderDispatcher(
    ReminderJournal journal, NotificationSecretStore secrets, NotificationProviders providers,
    Func<TrackerState> state, Func<ReminderRule[]> rules, Func<PhonePolicy> phone,
    Func<IReadOnlyDictionary<string, DateTimeOffset>> legacySent,
    Func<IReadOnlyList<ReminderOccurrence>, DeliveryResult> windows, TimeProvider? clock = null,
    Func<bool>? expectedResetNotifications = null, Func<ResetAlert[]>? resetAlerts = null, Func<bool>? newCreditNotifications = null,
    Func<bool>? reserveAdvice = null,
    Func<ReminderOccurrence, CancellationToken, Task<DeliveryResult>>? gmail = null, Func<bool>? gmailReady = null, Func<bool>? reserveMail = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextPoll;
    public string? Error { get; private set; }
    public IReadOnlyList<ReminderDelivery> History => journal.Entries;

    public async Task TickAsync(CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return;
        try
        {
            Error = null;
            var now = _clock.GetUtcNow(); journal.Prune(now);
            var due = Due(now);
            var valid = due.Select(r => r.Key).ToHashSet();
            foreach (var pending in journal.Entries.Where(d => d.Status == DeliveryStatus.Deferred && !valid.Contains(d.Occurrence.Key)).ToArray())
                journal.Put(pending with { Status = DeliveryStatus.Cancelled, UpdatedAt = now, Detail = Loc.T("Échéance dépassée, règle désactivée ou événement modifié.") });
            var desktop = new List<ReminderOccurrence>();
            foreach (var group in due.GroupBy(ReminderPlanner.EventKey))
            {
                // Credit notices (new, use now) share the credit's expiry but never replace its expiry reminders.
                var urgent = group.Where(r => !Standalone(r)).Select(r => r.LeadMinutes).DefaultIfEmpty(0).Min();
                foreach (var r in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (journal.Entries.Any(d => d.Occurrence.Key == r.Key && d.Status != DeliveryStatus.Deferred)) continue;
                    if (Standalone(r))
                    {
                        if (Due(_clock.GetUtcNow()).Any(x => x.Key == r.Key)) desktop.Add(r);
                        continue;
                    }
                    if (r.LeadMinutes == 0 && r.Channel == ReminderChannel.Windows && journal.Entries.Any(d =>
                        d.Occurrence.LeadMinutes == 0 && ReminderPlanner.EventKey(d.Occurrence) == ReminderPlanner.EventKey(r) &&
                        d.Status is DeliveryStatus.Submitting or DeliveryStatus.Shown or DeliveryStatus.Accepted or DeliveryStatus.Delivered or DeliveryStatus.Completed or DeliveryStatus.Unknown)) continue;
                    // Recheck after every await: deleted accounts/disabled rules cannot leave an active queue behind.
                    if (!Due(_clock.GetUtcNow()).Any(x => x.Key == r.Key)) continue;
                    var legacy = r.Kind == ResetKind.Reserve && legacySent().ContainsKey(CalendarExport.Identity(r.AccountId, "credit", r.CreditId!, r.At));
                    if (r.LeadMinutes != urgent || legacy) { Put(r, DeliveryStatus.Skipped, legacy ? Loc.T("Rappel déjà envoyé avant migration.") : Loc.T("Rappel remplacé par le délai le plus proche.")); continue; }
                    if (r.Channel == ReminderChannel.Windows)
                    {
                        if (ReminderPlanner.IsGlobalReset(r) && state().GlobalResetFeed?.Error is not null)
                        { Defer(r, Loc.T("En attente d’une nouvelle vérification des sources publiques.")); continue; }
                        desktop.Add(r); continue;
                    }
                    var configuration = secrets.Read();
                    var viaGmail = ViaGmail(r.Channel, configuration);
                    if (!viaGmail && !NotificationProviders.Configured(r.Channel, configuration)) { Put(r, DeliveryStatus.Cancelled, Loc.T("Canal désactivé ou configuration incomplète.")); continue; }
                    if (r.Channel is ReminderChannel.Sms or ReminderChannel.Call)
                    {
                        if (ReminderPlanner.IsQuiet(_clock.GetUtcNow(), phone())) { Defer(r, Loc.T("Heures silencieuses.")); continue; }
                        if (ReminderPlanner.AtDailyLimit(r.Channel, _clock.GetUtcNow(), phone(), journal.Entries)) { Defer(r, Loc.T("Limite quotidienne atteinte.")); continue; }
                    }
                    var attempt = new ReminderDelivery(r, DeliveryStatus.Submitting, _clock.GetUtcNow(), Loc.T("Envoi en cours."), AttemptedAt: _clock.GetUtcNow());
                    journal.Put(attempt);
                    var result = viaGmail ? await gmail!(r, token) : await providers.SendAsync(r, configuration, token);
                    journal.Put(attempt with { Status = result.Status, Detail = result.Detail, ProviderId = result.ProviderId, UpdatedAt = _clock.GetUtcNow() });
                }
            }
            if (desktop.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = Due(_clock.GetUtcNow()).Select(r => r.Key).ToHashSet();
                desktop = desktop.Where(r => current.Contains(r.Key) &&
                    (!ReminderPlanner.IsGlobalReset(r) || state().GlobalResetFeed?.Error is null)).ToList();
                foreach (var r in desktop) Put(r, DeliveryStatus.Submitting, Loc.T("Transmission à Windows."));
                if (desktop.Count > 0)
                {
                    var result = SendWindows(desktop);
                    foreach (var r in desktop) Put(r, result.Status, result.Detail);
                }
            }
            if (now >= _nextPoll)
            {
                _nextPoll = now.AddMinutes(1);
                foreach (var delivery in journal.Entries.Where(d => d.Status == DeliveryStatus.Accepted && d.ProviderId is not null && d.AttemptedAt > now.AddDays(-1)).Take(20).ToArray())
                {
                    var result = await providers.PollAsync(delivery, secrets.Read(), token);
                    if (result is not null) journal.Put(delivery with { Status = result.Status, Detail = result.Detail, UpdatedAt = _clock.GetUtcNow() });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { Error = Loc.T("Rappels suspendus : impossible de lire ou d’enregistrer les données locales."); }
        finally { _gate.Release(); }
    }
    private IReadOnlyList<ReminderOccurrence> Due(DateTimeOffset now)
    {
        var current = state();
        var requested = ResetAlertPlanner.Due(current, resetAlerts?.Invoke() ?? [], now);
        return ReminderPlanner.Due(current, rules(), now)
            .Concat(requested)
            .Concat(expectedResetNotifications?.Invoke() == true ? ExpectedReset.Due(current, now)
                .Where(r => !requested.Any(a => ReminderPlanner.EventKey(a) == ReminderPlanner.EventKey(r))) : [])
            .Concat(newCreditNotifications?.Invoke() == true ? NewResetCredits.Due(current, now) : [])
            .Concat(reserveAdvice?.Invoke() == true ? ReserveAdvice.Due(current, now) : [])
            // The default email alert: a day before a credit would be lost, once Gmail sending is granted.
            .Concat(reserveMail?.Invoke() == true && gmailReady?.Invoke() == true ? ReminderPlanner.Due(current, [ReserveMailRule], now) : [])
            .ToArray();
    }
    public static readonly ReminderRule ReserveMailRule = new(ResetKind.Reserve, true, [1440], [ReminderChannel.Email]);
    /// <summary>Email goes through the user's Gmail when no SendGrid connector is configured and Gmail sending is granted.</summary>
    private bool ViaGmail(ReminderChannel channel, NotificationSecrets configuration) => channel == ReminderChannel.Email && gmail is not null &&
        gmailReady?.Invoke() == true && !NotificationProviders.Configured(ReminderChannel.Email, configuration);
    private static bool Standalone(ReminderOccurrence r) => NewResetCredits.Is(r) || ReserveAdvice.Is(r);
    private void Put(ReminderOccurrence r, DeliveryStatus status, string text) => journal.Put(new(r, status, _clock.GetUtcNow(), text));
    private DeliveryResult SendWindows(IReadOnlyList<ReminderOccurrence> occurrences)
    {
        try { return windows(occurrences); }
        catch (Exception) { return new(DeliveryStatus.Failed, Loc.T("Notification Windows impossible. Vérifiez les réglages de notification Windows.")); }
    }
    private void Defer(ReminderOccurrence r, string text)
    {
        if (!journal.Entries.Any(d => d.Occurrence.Key == r.Key && d.Status == DeliveryStatus.Deferred && d.Detail == text)) Put(r, DeliveryStatus.Deferred, text);
    }
    public async Task<DeliveryResult> TestAsync(ReminderChannel channel, CancellationToken token = default, Func<bool>? stillAuthorized = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (stillAuthorized is not null && !stillAuthorized()) return new(DeliveryStatus.Skipped, Loc.T("Configuration modifiée pendant l’attente ; test annulé."));
            var now = _clock.GetUtcNow();
            var viaGmail = ViaGmail(channel, secrets.Read());
            if (channel != ReminderChannel.Windows && !viaGmail && !NotificationProviders.Configured(channel, secrets.Read())) return new(DeliveryStatus.Failed, Loc.T("Enregistrez et activez d’abord le connecteur."));
            if (channel is ReminderChannel.Sms or ReminderChannel.Call && (ReminderPlanner.IsQuiet(now, phone()) || ReminderPlanner.AtDailyLimit(channel, now, phone(), journal.Entries)))
                return new(DeliveryStatus.Skipped, Loc.T("Test bloqué par les heures silencieuses ou la limite quotidienne."));
            var r = new ReminderOccurrence("test/" + Guid.NewGuid(), Guid.Empty, Loc.T("Test de notification"), ResetKind.Weekly, null, now.AddHours(1), now, 60, channel);
            var attempt = new ReminderDelivery(r, DeliveryStatus.Submitting, now, Loc.T("Test demandé manuellement."), AttemptedAt: now, IsTest: true); journal.Put(attempt);
            DeliveryResult result;
            if (channel == ReminderChannel.Windows) result = SendWindows([r]);
            else if (viaGmail) result = await gmail!(r, token);
            else result = await providers.SendAsync(r, secrets.Read(), token);
            journal.Put(attempt with { Status = result.Status, Detail = result.Detail, ProviderId = result.ProviderId, UpdatedAt = _clock.GetUtcNow() });
            return result;
        }
        finally { _gate.Release(); }
    }
}
