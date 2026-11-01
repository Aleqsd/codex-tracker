using System.Net.Http;
using CodexTracker.Codex;

namespace CodexTracker.App;

internal sealed class ReminderRuntime : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _demo;
    private CancellationTokenSource _wake = new();
    private bool _paused;
    private readonly ReminderDispatcher? _dispatcher;
    internal NotificationSecretStore Secrets { get; }
    internal string? Error { get; private set; }
    internal IReadOnlyList<ReminderDelivery> History => _dispatcher?.History ?? [];
    internal Func<IReadOnlyList<ReminderOccurrence>, DeliveryResult>? ShowWindows { get; set; }
    internal ReminderRuntime(ITrackerService service, PreferencesStore preferences, bool demo, GoogleCalendarSync? google = null)
    {
        _demo = demo; Secrets = new(preferences.DataDirectory);
        try
        {
            _dispatcher = new(new ReminderJournal(preferences.DataDirectory), Secrets, new(_http), () => service.State,
                () => preferences.Current.ReminderRules!, () => preferences.Current.PhonePolicy,
                () => preferences.Current.SentExpiryReminders, rows => ShowWindows?.Invoke(rows)
                    ?? new(DeliveryStatus.Failed, Loc.T("Le canal de notification Windows n’est pas disponible. Rouvrez le tracker puis réessayez.")),
                expectedResetNotifications: () => preferences.Current.ResetNotifications,
                resetAlerts: () => preferences.Current.ResetAlerts,
                newCreditNotifications: () => preferences.Current.NewCreditNotifications,
                reserveAdvice: () => preferences.Current.ReserveAdviceNotifications,
                gmail: google is null ? null : (reminder, token) => google.SendMailAsync(reminder, token), gmailReady: () => google?.CanMail == true,
                reserveMail: () => preferences.Current.ReserveMailAlerts,
                pushMirror: () => preferences.Current.PushMirror,
                smartReminders: () => preferences.Current.SmartReminders);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Error = Loc.T("Le journal des rappels est illisible. Les envois sont suspendus pour éviter les doublons."); }
    }
    internal async Task TickAsync()
    {
        if (_demo || _paused || _dispatcher is null || ShowWindows is null || _lifetime.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _wake.Token);
        try { await _dispatcher.TickAsync(linked.Token); Error = _dispatcher.Error; }
        catch (OperationCanceledException) { }
    }
    internal Task<DeliveryResult> TestAsync(ReminderChannel channel, Func<bool>? stillAuthorized = null)
    {
        if (_demo && !(channel == ReminderChannel.Email && GoogleCalendarSync.LiveDemo)) return Task.FromResult(new DeliveryResult(DeliveryStatus.Skipped, Loc.T("Tests d’envoi désactivés dans la démonstration.")));
        if (_dispatcher is null) return Task.FromResult(new DeliveryResult(DeliveryStatus.Failed, Error ?? Loc.T("Le moteur de rappels est indisponible.")));
        if (_paused || _lifetime.IsCancellationRequested) return Task.FromResult(new DeliveryResult(DeliveryStatus.Skipped, Loc.T("Le tracker est suspendu ou en cours de fermeture. Réessayez après sa reprise.")));
        return _dispatcher.TestAsync(channel, _lifetime.Token, stillAuthorized);
    }
    /// <summary>Copies a Windows notification shown outside the reminder engine (quota alerts) to the phone, once per key.</summary>
    internal async Task MirrorAsync(string key, string title, string body)
    {
        if (_demo || _paused || _dispatcher is null || _lifetime.IsCancellationRequested) return;
        try { await _dispatcher.MirrorAsync(key, title, body, _lifetime.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { /* A failed copy never blocks the Windows notification; the journal keeps what was attempted. */ }
    }
    internal void Pause() { _paused = true; _wake.Cancel(); }
    internal void Resume() { if (!_paused) return; _wake = new(); _paused = false; }
    public void Dispose() { _lifetime.Cancel(); _wake.Cancel(); _http.Dispose(); }
}
