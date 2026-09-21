using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed class ReminderDispatcher(
    ReminderJournal journal, NotificationSecretStore secrets, NotificationProviders providers,
    Func<TrackerState> state, Func<ReminderRule[]> rules, Func<PhonePolicy> phone,
    Func<IReadOnlyDictionary<string, DateTimeOffset>> legacySent,
    Action<IReadOnlyList<ReminderOccurrence>> windows, TimeProvider? clock = null)
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
            var due = ReminderPlanner.Due(state(), rules(), now);
            var valid = due.Select(r => r.Key).ToHashSet();
            foreach (var pending in journal.Entries.Where(d => d.Status == DeliveryStatus.Deferred && !valid.Contains(d.Occurrence.Key)).ToArray())
                journal.Put(pending with { Status = DeliveryStatus.Cancelled, UpdatedAt = now, Detail = "Échéance dépassée, règle désactivée ou événement modifié." });
            var desktop = new List<ReminderOccurrence>();
            foreach (var group in due.GroupBy(ReminderPlanner.EventKey))
            {
                var urgent = group.Min(r => r.LeadMinutes);
                foreach (var r in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (journal.Entries.Any(d => d.Occurrence.Key == r.Key && d.Status != DeliveryStatus.Deferred)) continue;
                    // Recheck after every await: deleted accounts/disabled rules cannot leave an active queue behind.
                    if (!ReminderPlanner.Due(state(), rules(), _clock.GetUtcNow()).Any(x => x.Key == r.Key)) continue;
                    var legacy = r.Kind == ResetKind.Reserve && legacySent().ContainsKey(CalendarExport.Identity(r.AccountId, "credit", r.CreditId!, r.At));
                    if (r.LeadMinutes != urgent || legacy) { Put(r, DeliveryStatus.Skipped, legacy ? "Rappel déjà envoyé avant migration." : "Rappel remplacé par le délai le plus proche."); continue; }
                    if (r.Channel == ReminderChannel.Windows) { desktop.Add(r); continue; }
                    var configuration = secrets.Read();
                    if (!NotificationProviders.Configured(r.Channel, configuration)) { Put(r, DeliveryStatus.Cancelled, "Canal désactivé ou configuration incomplète."); continue; }
                    if (r.Channel is ReminderChannel.Sms or ReminderChannel.Call)
                    {
                        if (ReminderPlanner.IsQuiet(_clock.GetUtcNow(), phone())) { Defer(r, "Heures silencieuses."); continue; }
                        if (ReminderPlanner.AtDailyLimit(r.Channel, _clock.GetUtcNow(), phone(), journal.Entries)) { Defer(r, "Limite quotidienne atteinte."); continue; }
                    }
                    var attempt = new ReminderDelivery(r, DeliveryStatus.Submitting, _clock.GetUtcNow(), "Envoi en cours.", AttemptedAt: _clock.GetUtcNow());
                    journal.Put(attempt);
                    var result = await providers.SendAsync(r, configuration, token);
                    journal.Put(attempt with { Status = result.Status, Detail = result.Detail, ProviderId = result.ProviderId, UpdatedAt = _clock.GetUtcNow() });
                }
            }
            if (desktop.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = ReminderPlanner.Due(state(), rules(), _clock.GetUtcNow()).Select(r => r.Key).ToHashSet();
                desktop = desktop.Where(r => current.Contains(r.Key)).ToList();
                foreach (var r in desktop) Put(r, DeliveryStatus.Submitting, "Transmission à Windows.");
                if (desktop.Count > 0) windows(desktop);
                foreach (var r in desktop) Put(r, DeliveryStatus.Shown, "Transmis à Windows ; affichage soumis aux réglages de notification Windows.");
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
        { Error = "Rappels suspendus : impossible de lire ou d’enregistrer les données locales."; }
        finally { _gate.Release(); }
    }
    private void Put(ReminderOccurrence r, DeliveryStatus status, string text) => journal.Put(new(r, status, _clock.GetUtcNow(), text));
    private void Defer(ReminderOccurrence r, string text)
    {
        if (!journal.Entries.Any(d => d.Occurrence.Key == r.Key && d.Status == DeliveryStatus.Deferred && d.Detail == text)) Put(r, DeliveryStatus.Deferred, text);
    }
    public async Task<DeliveryResult> TestAsync(ReminderChannel channel, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var now = _clock.GetUtcNow();
            if (channel != ReminderChannel.Windows && !NotificationProviders.Configured(channel, secrets.Read())) return new(DeliveryStatus.Failed, "Enregistrez et activez d’abord le connecteur.");
            if (channel is ReminderChannel.Sms or ReminderChannel.Call && (ReminderPlanner.IsQuiet(now, phone()) || ReminderPlanner.AtDailyLimit(channel, now, phone(), journal.Entries)))
                return new(DeliveryStatus.Skipped, "Test bloqué par les heures silencieuses ou la limite quotidienne.");
            var r = new ReminderOccurrence("test/" + Guid.NewGuid(), Guid.Empty, "Test de notification", ResetKind.Weekly, null, now.AddHours(1), now, 60, channel);
            var attempt = new ReminderDelivery(r, DeliveryStatus.Submitting, now, "Test demandé manuellement.", AttemptedAt: now, IsTest: true); journal.Put(attempt);
            DeliveryResult result;
            if (channel == ReminderChannel.Windows) { windows([r]); result = new(DeliveryStatus.Shown, "Test transmis à Windows."); }
            else result = await providers.SendAsync(r, secrets.Read(), token);
            journal.Put(attempt with { Status = result.Status, Detail = result.Detail, ProviderId = result.ProviderId, UpdatedAt = _clock.GetUtcNow() });
            return result;
        }
        finally { _gate.Release(); }
    }
}
