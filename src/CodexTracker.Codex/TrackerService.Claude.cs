using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed partial class TrackerService
{
    private readonly ClaudeCodeLocation _claudeLocation;
    private readonly ClaudeCodeObservations _claudeObservations;
    private readonly IAccountUsageReader _claudeReader;
    private readonly IReadOnlyList<string> _claudeDesktopUsagePaths;
    private readonly List<AuthFileMonitor> _claudeMonitors = [];
    private CancellationTokenSource _claudeIdentityLifetime = new();
    private long _claudeGeneration;
    private long _claudeNotificationBaselineGeneration = -1;
    private string? _claudeKey;
    private string? _claudeAccountId;
    private Task? _claudeRefresh;
    private ActiveObservation? _claudeActiveObservation;

    private async Task DetectSourcesAsync(CancellationToken token)
    {
        await DetectAsync(token);
        if (_options.DetectClaudeCode) await DetectClaudeAsync(token);
    }

    private async Task<Task> QueueBothRefreshesAsync(CancellationToken token)
    {
        var codex = await QueueRefreshAsync(token);
        var claude = _options.DetectClaudeCode ? await QueueClaudeRefreshAsync(token) : Task.CompletedTask;
        return Task.WhenAll(codex, claude);
    }

    private void StartClaudeMonitors()
    {
        if (!_options.DetectClaudeCode || !_options.MonitorAuthChanges) return;
        foreach (var path in new[] { _claudeLocation.ConfigPath, _claudeLocation.CredentialsPath, _claudeObservations.SignalPath }
            .OfType<string>().Concat(_claudeDesktopUsagePaths).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var monitor = new AuthFileMonitor(path, async token =>
            {
                await DetectClaudeAsync(token);
                _ = await QueueClaudeRefreshAsync(token);
            }, _options.DetectionInterval);
            _claudeMonitors.Add(monitor);
            monitor.Start();
        }
    }

    private void RestartClaudeObservation()
    {
        _claudeIdentityLifetime.Cancel();
        _claudeIdentityLifetime.Dispose();
        _claudeIdentityLifetime = new();
        _claudeGeneration++;
        _claudeNotificationBaselineGeneration = -1;
        _claudeActiveObservation = null;
        _claudeRefresh = null;
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
            try { identity = await ClaudeCodeIdentity.ReadAsync(_claudeLocation, token); }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                warning = ex is TrackerException ? ex.Message : ex is FileNotFoundException or DirectoryNotFoundException
                    ? "Aucune session locale Claude Code détectée. Ouvrez Claude Code et connectez-vous."
                    : "La session locale Claude Code est illisible. Derniers comptes et relevés conservés ; vérifiez votre connexion dans Claude Code.";
            }
            var key = identity is null ? null : identity.Email.ToUpperInvariant() + "\n" + identity.AccountId;
            if (key == _claudeKey) return;
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
                StatusMessage = identity is not null ? "Compte Claude Code détecté · lecture des relevés locaux…"
                    : accounts.Any(a => a.IsActiveInCodex) ? State.StatusMessage : warning });
            Save();
        }
        finally { _gate.Release(); }
    }

    private async Task<Task> QueueClaudeRefreshAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed || _suspended || _claudeKey is null || _claudeAccountId is null) return Task.CompletedTask;
            if (_claudeRefresh is { IsCompleted: false }) return _claudeRefresh;
            var active = State.Accounts.Single(a => a.IsActiveInClaudeCode);
            var sequence = ++_requestSequence;
            _claudeRefresh = RefreshClaudeCoreAsync(active.Profile, _claudeAccountId, _claudeGeneration, _claudeIdentityLifetime.Token);
            _requests[sequence] = _claudeRefresh;
            _ = _claudeRefresh.ContinueWith(completed => _requests.TryRemove(sequence, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _claudeRefresh;
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
                Update(profile.Id, a => a with { IsRefreshing = true });
                Set(State with { IsBusy = true, StatusMessage = "Lecture des relevés Claude Code…" });
            }
            finally { _gate.Release(); }
            var candidate = await _claudeReader.ReadAsync(profile, accountId, request.Token);
            if (!SameEmail(candidate.Email, profile.Email) || !candidate.Buckets.Any(b => b.Id == "claude"))
                throw new TrackerException("Claude Code a retourné un autre compte ou un relevé invalide. Les données ont été ignorées.");
            snapshot = candidate;
        }
        catch (OperationCanceledException)
        {
            if (_lifetime.IsCancellationRequested || identityToken.IsCancellationRequested) return;
            error = "Le relevé Claude Code met trop de temps à répondre. Dernier relevé conservé.";
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            error = ex is TrackerException ? ex.Message : "Le relevé Claude Code est indisponible. Les dernières données sont conservées.";
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    await DetectClaudeAsync(_lifetime.Token);
                    await _gate.WaitAsync(_lifetime.Token);
                    try
                    {
                        if (generation == _claudeGeneration)
                        {
                            var previous = State.Accounts.FirstOrDefault(a => a.Profile.Id == profile.Id)?.Snapshot;
                            if (snapshot is not null && previous is not null && snapshot.FetchedAt < previous.FetchedAt)
                            { snapshot = null; error = "Un relevé Claude Code plus ancien a été ignoré. Les dernières données sont conservées."; }
                            if (snapshot is not null) notifications = RecordObservation(profile, snapshot, generation, claude: true);
                            Update(profile.Id, a => a with { Snapshot = snapshot ?? a.Snapshot, IsConnected = snapshot is not null || a.IsConnected, IsRefreshing = false, Error = error });
                            Set(State with { IsBusy = State.Accounts.Any(a => a.IsRefreshing), StatusMessage = error ?? "À jour · comptes Codex et Claude Code suivis automatiquement" });
                            Save();
                        }
                    }
                    finally { _gate.Release(); }
                    if (generation != _claudeGeneration && _claudeRefresh is null) _ = await QueueClaudeRefreshAsync(_lifetime.Token);
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
