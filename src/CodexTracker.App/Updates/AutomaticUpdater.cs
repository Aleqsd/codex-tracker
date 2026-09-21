using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTracker.App.Updates;

// One scheduler owned by the main application, never by a settings window or MCP bridge.
internal sealed class AutomaticUpdater(UpdateService service, Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private bool _enabled, _busy;
    internal DateTimeOffset NextCheck { get; private set; } = DateTimeOffset.MinValue;

    internal void SetEnabled(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        if (!enabled) _operation?.Cancel();
        else NextCheck = DateTimeOffset.MinValue;
    }

    internal async Task RunAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), _lifetime.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do { await TickAsync(); } while (await timer.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal async Task TickAsync()
    {
        if (!_enabled || _busy || _lifetime.IsCancellationRequested || _clock() < NextCheck) return;
        _busy = true;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        try
        {
            var result = await service.CheckDetailedAsync(operation.Token);
            NextCheck = result.IsVerifiedNow ? _clock().AddHours(6) : result.NextCheckAt ?? _clock().AddMinutes(15);
            if (NextCheck <= _clock()) NextCheck = _clock().AddMinutes(15);
            if (_enabled && result.IsVerifiedNow && result.Release is { } release)
                await service.PrepareAsync(release, operation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Keep collection and reminders alive when offline, out of disk space, or rate limited.
            NextCheck = _clock().AddMinutes(15);
        }
        finally { _operation = null; _busy = false; }
    }

    public void Dispose() { _enabled = false; _lifetime.Cancel(); _operation?.Cancel(); }
}
