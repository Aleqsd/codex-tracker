using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

/// <summary>No account input, no persisted evidence. A restart requires source verification again.</summary>
public sealed class GlobalResetMonitor(GlobalResetReader reader, Func<bool> enabled)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalResetFeedState _state = new([]);
    private DateTimeOffset _nextCheck;
    public GlobalResetFeedState? State => enabled() ? _state : null;
    public event EventHandler? Changed;

    public async Task CheckAsync(DateTimeOffset now, CancellationToken token = default)
    {
        if (!enabled() || now < _nextCheck || !await _gate.WaitAsync(0, token)) return;
        try
        {
            if (!enabled() || now < _nextCheck) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                var result = await reader.ReadAsync(now, timeout.Token);
                if (!enabled()) { _state = new([]); return; }
                _state = result;
                _nextCheck = now.AddMinutes(15);
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                _nextCheck = now.AddMinutes(30);
                // Previously verified evidence remains visibly dated and expires independently.
                _state = _state with { Error = "Vérification des annonces indisponible. Nouvel essai dans 30 min." };
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally { _gate.Release(); }
    }
}
