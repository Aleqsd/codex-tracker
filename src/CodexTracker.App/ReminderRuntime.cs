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
    internal Action<IReadOnlyList<ReminderOccurrence>>? ShowWindows { get; set; }
    internal ReminderRuntime(ITrackerService service, PreferencesStore preferences, bool demo)
    {
        _demo = demo; Secrets = new(preferences.DataDirectory);
        try
        {
            _dispatcher = new(new ReminderJournal(preferences.DataDirectory), Secrets, new(_http), () => service.State,
                () => preferences.Current.ReminderRules!, () => preferences.Current.PhonePolicy,
                () => preferences.Current.SentExpiryReminders, rows => ShowWindows?.Invoke(rows));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Error = "Le journal des rappels est illisible. Les envois sont suspendus pour éviter les doublons."; }
    }
    internal async Task TickAsync()
    {
        if (_demo || _paused || _dispatcher is null || ShowWindows is null || _lifetime.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, _wake.Token);
        try { await _dispatcher.TickAsync(linked.Token); Error = _dispatcher.Error; }
        catch (OperationCanceledException) { }
    }
    internal Task<DeliveryResult> TestAsync(ReminderChannel channel) => _demo || _dispatcher is null
        ? Task.FromResult(new DeliveryResult(DeliveryStatus.Skipped, "Tests d’envoi désactivés dans la démonstration.")) : _dispatcher.TestAsync(channel, _lifetime.Token);
    internal void Pause() { _paused = true; _wake.Cancel(); }
    internal void Resume() { if (!_paused) return; _wake = new(); _paused = false; }
    public void Dispose() { _lifetime.Cancel(); _wake.Cancel(); _http.Dispose(); }
}
