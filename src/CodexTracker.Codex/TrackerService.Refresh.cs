using System.Diagnostics;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed partial class TrackerService
{
    private sealed class CollectionSchedule(AccountProvider provider)
    {
        public ProviderRefreshStatus Status = new(provider, null, RefreshOutcome.Unavailable);
        public long Started, Finished, Activity;
    }

    private readonly CollectionSchedule _codexSchedule = new(AccountProvider.Codex);
    private readonly CollectionSchedule _claudeSchedule = new(AccountProvider.ClaudeCode);
    private bool _codexPending, _claudePending, _claudeCachePending;

    public IReadOnlyList<ProviderRefreshStatus> GetRefreshStatus() => (_options.DetectClaudeCode
        ? new[] { _codexSchedule, _claudeSchedule } : [_codexSchedule]).Select(schedule =>
    {
        var status = schedule.Status;
        return status with
        {
            Outcome = _suspended ? RefreshOutcome.Suspended : status.Outcome,
            NextAttemptAt = !_options.AutomaticRefresh || _suspended || _disposed || status.Outcome == RefreshOutcome.Refreshing
                ? null : DateTimeOffset.UtcNow + DelayUntilDue(schedule)
        };
    }).ToArray();

    private ProviderRefreshStatus StoppedStatus(bool claude) => (claude ? _claudeSchedule : _codexSchedule).Status with
        { Outcome = _suspended ? RefreshOutcome.Suspended : RefreshOutcome.Unavailable, NextAttemptAt = null };

    // Called under _gate. One bounded pass is returned to callers; a pending native write starts
    // another pass in the background, so a busy source cannot indefinitely extend a manual request.
    private Task<ProviderRefreshStatus> StartRefresh(AccountProfile profile, string accountId, long generation,
        CancellationToken identityToken, bool claude, RefreshReason reason)
    {
        var sequence = ++_requestSequence;
        var task = RunRefreshPassAsync(profile, accountId, generation, identityToken, claude, reason);
        if (claude) _claudeRefresh = task; else _refresh = task;
        _requests[sequence] = task;
        _ = task.ContinueWith(completed => _requests.TryRemove(sequence, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private Task<ProviderRefreshStatus> RunRefreshPassAsync(AccountProfile profile, string accountId, long generation,
        CancellationToken identityToken, bool claude, RefreshReason reason) => Task.Run(async () =>
    {
        var schedule = claude ? _claudeSchedule : _codexSchedule;
        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_suspended || generation != (claude ? _claudeGeneration : _generation)) return StoppedStatus(claude);
                schedule.Started = Stopwatch.GetTimestamp();
                schedule.Status = schedule.Status with { AccountId = profile.Id, Outcome = RefreshOutcome.Refreshing,
                    AttemptedAt = DateTimeOffset.UtcNow, CompletedAt = null, DurationMilliseconds = null,
                    Reason = reason == RefreshReason.Timer && schedule.Status.ConsecutiveFailures > 0 ? RefreshReason.Retry : reason,
                    ErrorCode = null };
            }
            finally { _gate.Release(); }
            if (claude) await RefreshClaudeCoreAsync(profile, accountId, generation, identityToken).ConfigureAwait(false);
            else await RefreshCoreAsync(profile, accountId, generation, identityToken).ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_suspended || _disposed) return StoppedStatus(claude);
                if (generation != (claude ? _claudeGeneration : _generation))
                    return new ProviderRefreshStatus(schedule.Status.Provider, profile.Id, RefreshOutcome.Unavailable, Reason: reason, ErrorCode: "account_changed");
                var result = schedule.Status with
                {
                    NextAttemptAt = _options.AutomaticRefresh && !_suspended && !_disposed
                        ? DateTimeOffset.UtcNow + DelayUntilDue(schedule) : null
                };
                var pending = claude ? _claudePending || _claudeCachePending && schedule.Status.ConsecutiveFailures == 0 : _codexPending;
                if (claude) { _claudePending = false; _claudeCachePending = false; } else _codexPending = false;
                if (claude) _claudeRefresh = null; else _refresh = null;
                if (pending && !_suspended && !_disposed)
                {
                    var current = State.Accounts.First(a => a.Profile.Id == profile.Id).Profile;
                    _ = StartRefresh(current, accountId, generation, identityToken, claude, RefreshReason.SourceChanged);
                }
                return result;
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || identityToken.IsCancellationRequested)
        { return StoppedStatus(claude); }
    });

    private void ResetSchedule(bool claude)
    {
        var schedule = claude ? _claudeSchedule : _codexSchedule;
        schedule.Started = schedule.Finished = schedule.Activity = 0;
        schedule.Status = new(schedule.Status.Provider, null, RefreshOutcome.Unavailable);
    }

    private void FinishRefresh(bool claude, AccountSnapshot? snapshot, AccountSnapshot? previous, string? error)
    {
        var schedule = claude ? _claudeSchedule : _codexSchedule;
        schedule.Finished = Stopwatch.GetTimestamp();
        if (snapshot is not null && previous is not null &&
            (snapshot.Weekly?.UsedPercent > previous.Weekly?.UsedPercent || snapshot.Short?.UsedPercent > previous.Short?.UsedPercent))
            schedule.Activity = schedule.Finished;
        schedule.Status = schedule.Status with
        {
            Outcome = snapshot is null ? RefreshOutcome.Failed : SnapshotContent.Equal(snapshot, previous) ? RefreshOutcome.Unchanged : RefreshOutcome.Updated,
            CompletedAt = DateTimeOffset.UtcNow, ObservedAt = (snapshot ?? previous)?.FetchedAt,
            DurationMilliseconds = Stopwatch.GetElapsedTime(schedule.Started).TotalMilliseconds,
            ConsecutiveFailures = snapshot is null ? schedule.Status.ConsecutiveFailures + 1 : 0,
            ErrorCode = error
        };
    }

    private void MarkUnavailable(bool claude, RefreshReason reason)
    {
        var schedule = claude ? _claudeSchedule : _codexSchedule;
        schedule.Started = schedule.Finished = Stopwatch.GetTimestamp();
        schedule.Status = new(schedule.Status.Provider, null, RefreshOutcome.Unavailable,
            AttemptedAt: DateTimeOffset.UtcNow, CompletedAt: DateTimeOffset.UtcNow, Reason: reason);
    }

    private bool IsDue(bool claude) => (claude ? _claudeRefresh : _refresh) is not { IsCompleted: false } &&
        DelayUntilDue(claude ? _claudeSchedule : _codexSchedule) <= TimeSpan.Zero;

    private TimeSpan DelayUntilDue(CollectionSchedule schedule)
    {
        if (schedule.Started == 0) return TimeSpan.Zero;
        var interval = RefreshPolicy.EffectiveInterval(_options.RefreshIntervalProvider?.Invoke() ?? _options.RefreshInterval,
            _options.ActiveRefreshIntervalProvider?.Invoke() ?? _options.RefreshInterval,
            schedule.Activity == 0 ? null : Stopwatch.GetElapsedTime(schedule.Activity));
        var normal = interval - Stopwatch.GetElapsedTime(schedule.Started);
        var retry = schedule.Finished == 0 ? TimeSpan.Zero : RefreshPolicy.RetryDelay(schedule.Status.ConsecutiveFailures) - Stopwatch.GetElapsedTime(schedule.Finished);
        return TimeSpan.FromTicks(Math.Max(0, Math.Max(normal.Ticks, retry.Ticks)));
    }

    private void UpdateCollection(Guid id, Func<AccountState, AccountState> update, string message)
    {
        var accounts = State.Accounts.Select(a => a.Profile.Id == id ? update(a) : a).ToArray();
        Set(State with { Accounts = accounts, IsBusy = accounts.Any(a => a.IsRefreshing), StatusMessage = message });
    }
}
