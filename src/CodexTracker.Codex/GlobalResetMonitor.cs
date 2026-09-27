using System.Net;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

/// <summary>No account input or persisted evidence; source checks are serial and revocable.</summary>
public sealed class GlobalResetMonitor(IGlobalResetReader reader, Func<bool> enabled)
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalResetFeedState _state = new([]);
    private bool _enabled = enabled();
    private long _generation;
    private CancellationTokenSource? _request;
    public GlobalResetFeedState? State { get { lock (_sync) return enabled() ? _state : null; } }
    public event EventHandler? Changed;

    // Called on every preference change, including a quick off/on between HTTP responses.
    public void Synchronize()
    {
        lock (_sync)
        {
            var next = enabled();
            if (next == _enabled) return;
            _enabled = next; _generation++;
            _request?.Cancel();
            // Toggling the setting must not bypass a provider/manual cooldown.
            _state = new([], LastAttemptAt: _state.LastAttemptAt,
                NextCheckAt: _state.ManualRetryAt, ManualRetryAt: _state.ManualRetryAt);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Suspend()
    {
        lock (_sync)
        {
            _generation++; _request?.Cancel();
            _state = _state with { NextCheckAt = _state.IsChecking ? _state.ManualRetryAt : _state.NextCheckAt, IsChecking = false };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CheckAsync(DateTimeOffset now, CancellationToken token = default, bool manual = false)
    {
        Synchronize();
        if (!await _gate.WaitAsync(0, token)) return;
        long generation = -1;
        CancellationTokenSource? request = null;
        try
        {
            lock (_sync)
            {
                if (!_enabled || !enabled() || now < (manual ? _state.ManualRetryAt : _state.NextCheckAt)) return;
                generation = _generation;
                request = CancellationTokenSource.CreateLinkedTokenSource(token);
                request.CancelAfter(TimeSpan.FromSeconds(45));
                _request = request;
                _state = _state with { IsChecking = true, LastAttemptAt = now, ManualRetryAt = now.AddMinutes(1) };
            }
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                var result = await reader.ReadAsync(now, request.Token);
                lock (_sync)
                {
                    if (generation != _generation || !enabled() || request.IsCancellationRequested) return;
                    _state = result with { LastAttemptAt = now, NextCheckAt = now.AddMinutes(15), ManualRetryAt = now.AddMinutes(1) };
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || generation != _generation || !enabled()) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                lock (_sync)
                {
                    if (generation != _generation || !enabled()) return;
                    // A missing/altered source is not equivalent to a transient outage.
                    var rejected = ex is InvalidDataException or JsonException || ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
                    var limited = ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
                    _state = _state with { Announcements = rejected ? [] : _state.Announcements, IsChecking = false,
                        Error = rejected ? "Source non vérifiable · estimations retirées." : limited ? "Source temporairement limitée · nouvel essai dans 30 min."
                            : _state.CheckedAt is null ? "Vérification impossible pour le moment." : "Sources indisponibles · dernier contrôle réussi conservé.",
                        NextCheckAt = now.AddMinutes(30), ManualRetryAt = now.AddMinutes(limited ? 30 : 1) };
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                if (generation == _generation) _state = _state with { IsChecking = false };
                if (ReferenceEquals(_request, request)) _request = null;
            }
            request?.Dispose(); _gate.Release();
            if (generation >= 0) Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
