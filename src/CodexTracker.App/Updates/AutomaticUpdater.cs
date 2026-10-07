using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTracker.App.Updates;

// One scheduler owned by the main application, never by a settings window or MCP bridge.
internal sealed class AutomaticUpdater : IDisposable
{
    private readonly UpdateService _service;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private bool _enabled, _busy;
    internal bool IsEnabled => _enabled;
    internal bool IsChecking => _busy;
    internal DateTimeOffset NextCheck { get; private set; } = DateTimeOffset.MinValue;

    internal AutomaticUpdater(UpdateService service, Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _service = service;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        service.ChannelChanged += ChannelChanged;
    }

    private void ChannelChanged(object? sender, EventArgs args)
    {
        _operation?.Cancel();
        NextCheck = DateTimeOffset.MinValue;
    }

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
            await _delay(TimeSpan.FromSeconds(15), _lifetime.Token);
            while (!_lifetime.IsCancellationRequested)
            {
                await TickAsync();
                // Poll at most once a minute for channel/option changes, but wake at the due time
                // when it is nearer. A fixed minute timer could miss it and add another minute.
                var remaining = NextCheck - _clock();
                var delay = _enabled && remaining > TimeSpan.Zero && remaining < TimeSpan.FromMinutes(1)
                    ? remaining : TimeSpan.FromMinutes(1);
                await _delay(delay, _lifetime.Token);
            }
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
            var result = await _service.CheckDetailedAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            NextCheck = result.IsVerifiedNow ? _clock().AddMinutes(15) : result.NextCheckAt ?? _clock().AddMinutes(15);
            if (NextCheck <= _clock()) NextCheck = _clock().AddMinutes(15);
            if (_enabled && result.IsVerifiedNow && result.Release is { } release)
                await _service.PrepareAsync(release, operation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Keep collection and reminders alive when offline, out of disk space, or rate limited.
            NextCheck = _clock().AddMinutes(15);
        }
        finally { _operation = null; _busy = false; }
    }

    public void Dispose() { _service.ChannelChanged -= ChannelChanged; _enabled = false; _lifetime.Cancel(); _operation?.Cancel(); }
}
