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
    internal ReminderRuntime(ITrackerService service, PreferencesStore preferences, bool demo)
    {
        _demo = demo; Secrets = new(preferences.DataDirectory);
        try
        {
            _dispatcher = new(new ReminderJournal(preferences.DataDirectory), Secrets, new(_http), () => service.State,
                () => preferences.Current.ReminderRules!, () => preferences.Current.PhonePolicy,
                () => preferences.Current.SentExpiryReminders, rows => ShowWindows?.Invoke(rows)
                    ?? new(DeliveryStatus.Failed, "Le canal de notification Windows n’est pas disponible. Rouvrez le tracker puis réessayez."));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Error = "Le journal des rappels est illisible. Les envois sont suspendus pour éviter les doublons."; }
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
        if (_demo) return Task.FromResult(new DeliveryResult(DeliveryStatus.Skipped, "Tests d’envoi désactivés dans la démonstration."));
        if (_dispatcher is null) return Task.FromResult(new DeliveryResult(DeliveryStatus.Failed, Error ?? "Le moteur de rappels est indisponible."));
        if (_paused || _lifetime.IsCancellationRequested) return Task.FromResult(new DeliveryResult(DeliveryStatus.Skipped, "Le tracker est suspendu ou en cours de fermeture. Réessayez après sa reprise."));
        return _dispatcher.TestAsync(channel, _lifetime.Token, stillAuthorized);
    }
    internal void Pause() { _paused = true; _wake.Cancel(); }
    internal void Resume() { if (!_paused) return; _wake = new(); _paused = false; }
    public void Dispose() { _lifetime.Cancel(); _wake.Cancel(); _http.Dispose(); }
}
