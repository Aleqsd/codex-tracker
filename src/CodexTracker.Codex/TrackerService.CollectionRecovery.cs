using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed partial class TrackerService
{
    private static readonly TimeSpan ObservationClockTolerance = TimeSpan.FromMinutes(1);
    private readonly HashSet<Guid> _clockRecoveryNeeded = [];
    private readonly HashSet<Guid> _unpersistedTelemetry = [];

    private static bool IsFutureObservation(DateTimeOffset observedAt, DateTimeOffset now) => observedAt > now + ObservationClockTolerance;

    private static bool HasFutureTelemetry(AccountTelemetry telemetry, DateTimeOffset now) =>
        telemetry.Samples.Any(s => IsFutureObservation(s.Timestamp, now)) ||
        telemetry.Alerts.Weekly is { } weekly && IsFutureObservation(weekly.ObservedAt, now) ||
        telemetry.Alerts.Short is { } shortWindow && IsFutureObservation(shortWindow.ObservedAt, now);

    // The first coherent observation after a clock correction is a silent baseline. Keep deduplication
    // for the same quota period, but never retain a future observation date that would freeze the evaluator.
    private static QuotaWindowAlertState? RebaseAlert(QuotaWindowAlertState? previous, QuotaWindow? window, DateTimeOffset observedAt)
    {
        if (window is null) return null;
        var samePeriod = previous is not null && previous.ResetsAt == window.ResetsAt;
        return new(window.RemainingPercent, window.ResetsAt, observedAt,
            samePeriod ? previous!.NotifiedThresholdMask : 0, window.ResetsAt,
            samePeriod && previous!.ForecastNotifiedFor == window.ResetsAt ? previous.ForecastNotifiedFor : null);
    }

    private void SaveCollectedState(IReadOnlyList<AccountState> accounts)
    {
        _store.SaveSettings(new(accounts.Select(a => a.Profile).ToList(), State.SelectedAccountId,
            State.OnboardingComplete, accounts.Where(a => a.IsConnected).Select(a => a.Profile.Id).ToList()));
        _store.SaveSnapshots(accounts.Where(a => a.Snapshot is not null).ToDictionary(a => a.Profile.Id, a => a.Snapshot!));
    }

    private async Task<IReadOnlyList<QuotaNotification>> CompleteCollectionAsync(AccountProfile profile, long generation,
        AccountSnapshot? snapshot, string? error, CodexFailureCode failure, bool claude)
    {
        var persistenceFailed = false;
        // Detection updates identity before persisting its display metadata. A failed save must not skip
        // finalization, and a changed generation must still discard the former identity's quota response.
        try
        {
            if (claude) await DetectClaudeAsync(_lifetime.Token);
            else await DetectAsync(_lifetime.Token);
        }
        catch (Exception ex) when (IsRecoverable(ex)) { persistenceFailed = true; }

        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            if (_disposed || _suspended || generation != (claude ? _claudeGeneration : _generation)) return [];
            var previous = State.Accounts.FirstOrDefault(a => a.Profile.Id == profile.Id)?.Snapshot;
            var now = DateTimeOffset.UtcNow;
            var diagnostic = error is null ? null : claude ? "collection_failed" : failure.ToString();
            if (snapshot is not null && (snapshot.FetchedAt < DateTimeOffset.UnixEpoch || IsFutureObservation(snapshot.FetchedAt, now)))
            {
                snapshot = null;
                error = Loc.T("La date du relevé est incohérente. Les dernières données sont conservées.");
                diagnostic = "invalid_observation_time";
                failure = CodexFailureCode.Unknown;
            }
            else if (snapshot is not null && previous is not null && snapshot.FetchedAt < previous.FetchedAt && !IsFutureObservation(previous.FetchedAt, now))
            {
                snapshot = null;
                error = claude ? Loc.T("Un relevé Claude Code plus ancien a été ignoré. Les dernières données sont conservées.")
                    : Loc.T("Un relevé plus ancien a été ignoré. Les dernières données sont conservées.");
                diagnostic = claude ? "collection_failed" : CodexFailureCode.Unknown.ToString();
                failure = CodexFailureCode.Unknown;
            }

            var telemetry = _telemetry.GetValueOrDefault(profile.Id) ?? AccountTelemetry.Empty;
            var clockRecovery = _clockRecoveryNeeded.Contains(profile.Id) || previous is not null && IsFutureObservation(previous.FetchedAt, now) || HasFutureTelemetry(telemetry, now);
            if (snapshot is not null && clockRecovery)
            {
                try { _store.PreserveClockRecovery(profile.Id, previous); }
                catch (Exception ex) when (IsRecoverable(ex)) { snapshot = null; persistenceFailed = true; }
            }
            var unchanged = SnapshotContent.Equal(snapshot, previous);
            IReadOnlyList<QuotaNotification> notifications = [];
            if (snapshot is not null && (!unchanged || _unpersistedTelemetry.Contains(profile.Id) || clockRecovery ||
                (claude ? _claudeNotificationBaselineGeneration : _notificationBaselineGeneration) != generation))
            {
                notifications = RecordObservation(profile, snapshot, generation, out var persisted, claude, clockRecovery);
                persistenceFailed |= !persisted;
            }

            if (!claude)
                _compatibility = _compatibility with
                {
                    Service = snapshot is not null ? CodexServiceStatus.Compatible : failure switch
                    {
                        CodexFailureCode.CodexNotFound => CodexServiceStatus.Missing,
                        CodexFailureCode.ProtocolUnsupported => CodexServiceStatus.Incompatible,
                        CodexFailureCode.SessionExpired => _compatibility.Service,
                        _ => CodexServiceStatus.Unavailable
                    },
                    LastFailure = failure, LastSuccessfulReadAt = snapshot?.FetchedAt ?? _compatibility.LastSuccessfulReadAt,
                    CheckedAt = now
                };

            var collected = State.Accounts.Select(a => a.Profile.Id == profile.Id ? a with
            {
                Snapshot = unchanged ? a.Snapshot : snapshot ?? a.Snapshot,
                IsConnected = claude ? snapshot is not null || a.IsConnected : a.IsConnected,
                IsRefreshing = false, Error = error
            } : a).ToArray();
            try { SaveCollectedState(collected); }
            catch (Exception ex) when (IsRecoverable(ex)) { persistenceFailed = true; }
            if (persistenceFailed)
            {
                error = Loc.T("Relevé conservé en mémoire ; enregistrement local indisponible.");
                diagnostic = "persistence_failed";
                collected = collected.Select(a => a.Profile.Id == profile.Id ? a with { Error = error } : a).ToArray();
            }
            // Finish even when detection, telemetry or account persistence failed. A valid in-memory
            // quota stays usable, while the failed outcome participates in the normal retry policy.
            FinishRefresh(claude, persistenceFailed ? null : snapshot, persistenceFailed ? snapshot ?? previous : previous, diagnostic);
            Set(State with { Accounts = collected, IsBusy = collected.Any(a => a.IsRefreshing), StatusMessage = error ??
                (claude ? Loc.T("À jour · comptes Codex et Claude Code suivis automatiquement") : Loc.T("À jour · changements de compte détectés automatiquement")) });
            return notifications;
        }
        finally { _gate.Release(); }
    }
}
