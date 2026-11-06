using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed partial class TrackerService
{
    private readonly ClaudeCodeLocation _claudeLocation;
    private readonly ClaudeCodeObservations _claudeObservations;
    private readonly IAccountUsageReader _claudeReader;
    private IReadOnlyList<string> _claudeDesktopUsagePaths;
    private readonly Func<IReadOnlyList<string>>? _claudeDesktopPathsProvider;
    private long _claudePathsDiscoveredAt;
    private readonly Dictionary<string, AuthFileMonitor> _claudeMonitors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _retiredClaudeMonitors = [];
    private ClaudeDesktopSourceMonitor? _claudeSourceMonitor;
    private CancellationTokenSource _claudeIdentityLifetime = new();
    private long _claudeGeneration;
    private long _claudeNotificationBaselineGeneration = -1;
    private string? _claudeKey;
    private string? _claudeAccountId;
    private Task<ProviderRefreshStatus>? _claudeRefresh;
    private ActiveObservation? _claudeActiveObservation;

    private async Task DetectSourcesAsync(CancellationToken token)
    {
        await DetectAsync(token);
        if (_options.DetectClaudeCode)
        {
            await RediscoverClaudeSourcesAsync(token);
            await DetectClaudeAsync(token);
        }
    }

    private async Task<Task<ProviderRefreshStatus[]>> QueueBothRefreshesAsync(CancellationToken token, RefreshReason reason = RefreshReason.Manual)
    {
        var codex = await QueueRefreshAsync(token, reason);
        return _options.DetectClaudeCode ? Task.WhenAll(codex, await QueueClaudeRefreshAsync(token, reason)) : Task.WhenAll(codex);
    }

    private void StartClaudeMonitors()
    {
        if (!_options.DetectClaudeCode || !_options.MonitorAuthChanges) return;
        var paths = new[] { _claudeLocation.ConfigPath, _claudeLocation.CredentialsPath, _claudeObservations.SignalPath }
            .OfType<string>().Concat(_claudeDesktopUsagePaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsolete in _claudeMonitors.Keys.Where(path => !paths.Contains(path)).ToArray())
        {
            var monitor = _claudeMonitors[obsolete];
            _claudeMonitors.Remove(obsolete);
            // The callback can be waiting for _gate: never await its shutdown while holding that gate.
            _retiredClaudeMonitors.Add(monitor.DisposeAsync().AsTask());
        }
        _retiredClaudeMonitors.RemoveAll(task => task.IsCompletedSuccessfully);
        foreach (var path in paths.Where(path => !_claudeMonitors.ContainsKey(path)))
        {
            var monitor = new AuthFileMonitor(path, async token =>
            {
                await DetectClaudeAsync(token);
                _ = await QueueClaudeRefreshAsync(token, RefreshReason.SourceChanged);
            }, _options.DetectionInterval, signalInitial: false);
            _claudeMonitors.Add(path, monitor);
            monitor.Start();
        }
        if (_claudeSourceMonitor is not null) { _claudeSourceMonitor.UpdatePaths(_claudeDesktopUsagePaths); return; }
        _claudeSourceMonitor = new(_claudeDesktopUsagePaths, async (changes, token) =>
        {
            var cacheOnly = (changes & ClaudeDesktopChange.Metadata) == 0;
            // Chromium writes many entries unrelated to quota. Do not even rescan identities
            // during failure backoff for those unclassified writes; explicit sources still wake us.
            await _gate.WaitAsync(token);
            try
            {
                if (_disposed || _suspended) return;
                if (cacheOnly && _claudeRefresh is { IsCompleted: false }) { _claudeCachePending = true; return; }
                if (cacheOnly && DeferClaudeCache()) return;
            }
            finally { _gate.Release(); }
            await DetectClaudeAsync(token);
            _ = await QueueClaudeRefreshAsync(token, RefreshReason.SourceChanged, cacheOnly);
        }, _options.DetectionInterval);
        _claudeSourceMonitor.Start();
    }

    private async Task<bool> RediscoverClaudeSourcesAsync(CancellationToken token, bool force = true)
    {
        if (_claudeDesktopPathsProvider is null) return false;
        await _gate.WaitAsync(token);
        try
        {
            RequireInitialized();
            if (_suspended) return false;
            var interval = _options.RefreshIntervalProvider?.Invoke() ?? _options.RefreshInterval;
            if (!force && _claudePathsDiscoveredAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_claudePathsDiscoveredAt) < interval) return false;
            var paths = _claudeDesktopPathsProvider().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            _claudePathsDiscoveredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            if (paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_claudeDesktopUsagePaths)) return false;
            var previous = _claudeDesktopUsagePaths;
            Volatile.Write(ref _claudeDesktopUsagePaths, paths);
            ClaudeDesktopSelection.Release(previous.Where(path => !paths.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray());
            if (_claudeSourceMonitor is not null) StartClaudeMonitors();
            return true;
        }
        finally { _gate.Release(); }
    }

    private bool DeferClaudeCache() => _claudeSchedule.Status.ConsecutiveFailures > 0 && DelayUntilDue(_claudeSchedule) > TimeSpan.Zero;

    private void RestartClaudeObservation()
    {
        _claudeIdentityLifetime.Cancel();
        _claudeIdentityLifetime.Dispose();
        _claudeIdentityLifetime = new();
        _claudeGeneration++;
        _claudeNotificationBaselineGeneration = -1;
        _claudeActiveObservation = null;
        _claudeRefresh = null;
        _claudePending = false;
        _claudeCachePending = false;
        ResetSchedule(true);
    }

    private async Task DetectClaudeAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            RequireInitialized();
            if (_suspended) return;
            ClaudeCodeIdentity? identity = null;
            string? warning = null;
            try { identity = await ClaudeCodeIdentity.ReadSelectedAsync(_claudeLocation, _claudeDesktopUsagePaths, State.Accounts.Select(a => a.Profile), token); }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                warning = ex is TrackerException ? ex.Message : ex is FileNotFoundException or DirectoryNotFoundException
                    ? Loc.T("Aucune session locale Claude Code détectée. Ouvrez Claude Code et connectez-vous.")
                    : Loc.T("La session locale Claude Code est illisible. Derniers comptes et relevés conservés ; vérifiez votre connexion dans Claude Code.");
            }
            var key = identity is null ? null : identity.Email.ToUpperInvariant() + "\n" + identity.AccountId;
            if (key == _claudeKey)
            {
                if (identity is not null && State.Accounts.FirstOrDefault(a => a.IsActiveInClaudeCode) is { } active)
                {
                    var profile = active.Profile with { OrganizationName = identity.OrganizationName,
                        ProviderPlanType = identity.PlanType, ProviderPlanMultiplier = identity.PlanMultiplier };
                    if (profile != active.Profile) { Update(active.Profile.Id, a => a with { Profile = profile }); Save(); }
                }
                return;
            }
            RestartClaudeObservation();
            _claudeKey = key;
            _claudeAccountId = identity?.AccountId;
            var accounts = State.Accounts.Select(a => a.Profile.Provider == AccountProvider.ClaudeCode
                ? a with { IsActiveInClaudeCode = false, IsRefreshing = false } : a).ToList();
            if (identity is not null)
            {
                var index = accounts.FindIndex(a => a.Profile.Provider == AccountProvider.ClaudeCode &&
                    SameEmail(a.Profile.Email, identity.Email) && a.Profile.ProviderAccountId == identity.AccountId);
                if (index < 0)
                {
                    // A legacy Claude profile without an organization cannot safely donate its quotas.
                    accounts.Add(new(new(Guid.NewGuid(), identity.Email, AccountProvider.ClaudeCode, identity.AccountId)));
                    index = accounts.Count - 1;
                }
                accounts[index] = accounts[index] with { IsActiveInClaudeCode = true,
                    IsConnected = identity.HasLocalCredential || accounts[index].Snapshot is not null, Error = null,
                    Profile = accounts[index].Profile with { OrganizationName = identity.OrganizationName,
                        ProviderPlanType = identity.PlanType, ProviderPlanMultiplier = identity.PlanMultiplier } };
                _claudeActiveObservation = new(accounts[index].Profile.Id, DateTimeOffset.UtcNow);
            }
            Set(State with { Accounts = accounts.ToArray(), IsBusy = accounts.Any(a => a.IsRefreshing),
                SelectedAccountId = accounts.Where(a => a.IsActive).OrderBy(a => a.Profile.Provider).FirstOrDefault()?.Profile.Id,
                StatusMessage = identity is not null ? Loc.T("Compte Claude Code détecté · lecture des relevés locaux…")
                    : accounts.Any(a => a.IsActiveInCodex) ? State.StatusMessage : warning });
            Save();
        }
        finally { _gate.Release(); }
    }

    private async Task<Task<ProviderRefreshStatus>> QueueClaudeRefreshAsync(CancellationToken token, RefreshReason reason = RefreshReason.Manual, bool cacheOnly = false)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed || _suspended) return Task.FromResult(StoppedStatus(true));
            if (_claudeKey is null || _claudeAccountId is null) { MarkUnavailable(true, reason); return Task.FromResult(_claudeSchedule.Status); }
            if (_claudeRefresh is { IsCompleted: false })
            {
                if (reason == RefreshReason.SourceChanged)
                { if (cacheOnly) _claudeCachePending = true; else _claudePending = true; }
                return _claudeRefresh;
            }
            if (cacheOnly && DeferClaudeCache()) return Task.FromResult(_claudeSchedule.Status);
            var active = State.Accounts.Single(a => a.IsActiveInClaudeCode);
            return StartRefresh(active.Profile, _claudeAccountId, _claudeGeneration, _claudeIdentityLifetime.Token, true, reason);
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshClaudeCoreAsync(AccountProfile profile, string accountId, long generation, CancellationToken identityToken)
    {
        await Task.Yield();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, identityToken);
        request.CancelAfter(_options.RequestTimeout);
        AccountSnapshot? snapshot = null;
        string? error = null;
        IReadOnlyList<QuotaNotification> notifications = [];
        try
        {
            await _gate.WaitAsync(request.Token);
            try
            {
                if (generation != _claudeGeneration) return;
                UpdateCollection(profile.Id, a => a with { IsRefreshing = true }, Loc.T("Lecture des relevés Claude Code…"));
            }
            finally { _gate.Release(); }
            var candidate = await _claudeReader.ReadAsync(profile, accountId, request.Token);
            if (!SameEmail(candidate.Email, profile.Email) || !candidate.Buckets.Any(b => b.Id == "claude"))
                throw new TrackerException(Loc.T("Claude Code a retourné un autre compte ou un relevé invalide. Les données ont été ignorées."));
            snapshot = candidate;
        }
        catch (OperationCanceledException)
        {
            if (_lifetime.IsCancellationRequested || identityToken.IsCancellationRequested) return;
            error = Loc.T("Le relevé Claude Code met trop de temps à répondre. Dernier relevé conservé.");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            error = ex is TrackerException ? ex.Message : Loc.T("Le relevé Claude Code est indisponible. Les dernières données sont conservées.");
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    notifications = await CompleteCollectionAsync(profile, generation, snapshot, error, CodexFailureCode.None, claude: true);
                    if (generation != _claudeGeneration && _claudeSchedule.Started == 0) _ = await QueueClaudeRefreshAsync(_lifetime.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (IsRecoverable(ex)) { }
            }
            foreach (var notification in notifications)
            {
                if (generation != _claudeGeneration || _suspended || _disposed) break;
                Notification?.Invoke(this, notification);
            }
        }
    }
}
