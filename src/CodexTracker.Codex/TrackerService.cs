using System.Collections.Concurrent;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed record TrackerServiceOptions
{
    public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker");
    public string? CodexExecutablePath { get; init; }
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(2);
    public Func<TimeSpan>? RefreshIntervalProvider { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public TimeSpan DetectionInterval { get; init; } = TimeSpan.FromSeconds(2);
    public bool AutomaticRefresh { get; init; } = true;
    public bool MonitorAuthChanges { get; init; } = true;
    public IReadOnlyList<string> RedirectedDataDirectories { get; init; } = [];
}

public interface IAccountUsageReader
{
    Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken);
}

public sealed class TrackerService : ITrackerService
{
    private readonly string? _authPath;
    private readonly TrackerServiceOptions _options;
    private readonly ProfileStore _store;
    private readonly IAccountUsageReader _reader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private readonly ConcurrentDictionary<Guid, AccountTelemetry> _telemetry = new();
    private CancellationTokenSource _identityLifetime = new();
    private AuthFileMonitor? _monitor;
    private Task? _timer;
    private Task? _refresh;
    private long _generation;
    private long _requestSequence;
    private long _lastRefreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
    private string? _activeKey;
    private string? _accountId;
    private bool _initialized;
    private bool _disposed;
    private bool _suspended;
    private long _notificationBaselineGeneration = -1;
    private sealed record ActiveObservation(Guid AccountId, DateTimeOffset StartedAt);
    private ActiveObservation? _activeObservation;
    private CodexCompatibilityDiagnostic _compatibility;
    public event EventHandler? Changed;
    public event EventHandler<QuotaNotification>? Notification;
    public TrackerState State { get; private set; } = new([], null, StatusMessage: "Détection du compte Codex…");
    public CodexCompatibilityDiagnostic GetCompatibilityDiagnostic() => _compatibility;
    public IReadOnlyList<string> RecoveryWarnings => _store.RecoveryWarnings;

    public TrackerService(string? authFilePath = null, TrackerServiceOptions? options = null, IAccountUsageReader? reader = null)
    {
        var location = CodexAuthLocation.Resolve(authFilePath, Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _authPath = location.Path;
        _compatibility = new(location.Source, CodexSessionStatus.NotChecked, CodexServiceStatus.NotChecked,
            CodexFailureCode.None, null, null);
        _options = options ?? new();
        _store = new(_options.DataDirectory);
        _reader = reader ?? new CurrentAccountUsageReader(_authPath ?? "", _store, _options);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) throw new OperationCanceledException("Codex Tracker se ferme.");
            if (_initialized) return;
            _store.Open();
            _store.RecoverRedirectedStores(_options.RedirectedDataDirectories);
            var settings = _store.LoadSettings();
            var snapshots = _store.LoadSnapshots();
            foreach (var profile in settings.Accounts) _telemetry[profile.Id] = _store.LoadTelemetry(profile.Id, DateTimeOffset.UtcNow);
            Set(State with
            {
                Accounts = settings.Accounts.Select(p => new AccountState(p,
                    snapshots.TryGetValue(p.Id, out var snapshot) && SameEmail(snapshot.Email, p.Email) ? snapshot : null,
                    IsConnected: settings.DetectedAccountIds?.Contains(p.Id) == true || snapshots.ContainsKey(p.Id))).ToArray(),
                SelectedAccountId = null,
                OnboardingComplete = settings.OnboardingComplete
            });
            _initialized = true;
        }
        finally { _gate.Release(); }
        await DetectAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireInitialized();
            if (_options.MonitorAuthChanges && _authPath is not null)
            {
                _monitor = new(_authPath, async token =>
                {
                    await DetectAsync(token);
                    _ = await QueueRefreshAsync(token);
                }, _options.DetectionInterval);
                _monitor.Start();
            }
            if (_options.AutomaticRefresh) _timer = RunTimerAsync();
        }
        finally { _gate.Release(); }
        await (await QueueRefreshAsync(cancellationToken)).WaitAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await DetectAsync(cancellationToken);
        await (await QueueRefreshAsync(cancellationToken)).WaitAsync(cancellationToken);
    }

    public Task ImportCurrentAccountAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);

    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || _suspended) return;
            _suspended = true;
            RestartObservation();
            Set(State with { IsBusy = false, Accounts = State.Accounts.Select(a => a with { IsRefreshing = false }).ToArray(),
                StatusMessage = "Suivi en pause pendant la veille" });
        }
        finally { _gate.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) throw new OperationCanceledException("Codex Tracker se ferme.");
            _suspended = false;
            RestartObservation();
            if (!_initialized) return;
            var active = State.Accounts.FirstOrDefault(a => a.IsActiveInCodex);
            if (active is not null) _activeObservation = new(active.Profile.Id, DateTimeOffset.UtcNow);
            Set(State with { IsBusy = false, Accounts = State.Accounts.Select(a => a with { IsRefreshing = false }).ToArray(),
                StatusMessage = "Sortie de veille · vérification du compte Codex…" });
        }
        finally { _gate.Release(); }
        await RefreshAsync(cancellationToken);
    }

    private void RestartObservation()
    {
        _identityLifetime.Cancel();
        _identityLifetime.Dispose();
        _identityLifetime = new();
        _generation++;
        _notificationBaselineGeneration = -1;
        _activeObservation = null;
        _refresh = null;
    }

    public IReadOnlyList<UsageSample> GetHistory(Guid accountId) => _telemetry.TryGetValue(accountId, out var telemetry)
        ? telemetry.Samples : Array.Empty<UsageSample>();

    public UsageForecast GetForecast(Guid accountId)
    {
        var state = State.Accounts.FirstOrDefault(a => a.Profile.Id == accountId);
        var observation = _activeObservation;
        if (state is not { IsActiveInCodex: true } || observation is null || observation.AccountId != accountId)
            return new(null, null, "Ouvrez ce compte dans Codex pour estimer sa consommation actuelle.");
        if (state.Error is not null) return new(null, null, "L'estimation est suspendue jusqu'au prochain relevé disponible.");
        var now = DateTimeOffset.UtcNow;
        var since = observation.StartedAt > now.AddHours(-1) ? observation.StartedAt : now.AddHours(-1);
        var samples = GetHistory(accountId).Where(s => s.Timestamp >= since).ToArray();
        return UsageAnalytics.Estimate(samples, now);
    }

    private async Task DetectAsync(CancellationToken cancellationToken)
    {
        // Serialise only local reads and state changes; a slow quota request cannot block detection.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireInitialized();
            if (_suspended) return;
            AuthDocument? identity = null;
            string? warning = null;
            try
            {
                if (_authPath is null) throw new TrackerException(
                    "Le dossier CODEX_HOME est invalide. Corrigez cette variable puis relancez le tracker.", CodexFailureCode.InvalidHome);
                identity = await CurrentAccountUsageReader.ReadIdentityAsync(_authPath, cancellationToken);
                var expired = identity.AccessTokenExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow;
                _compatibility = _compatibility with
                {
                    Session = expired ? CodexSessionStatus.Expired : CodexSessionStatus.Detected,
                    CheckedAt = DateTimeOffset.UtcNow,
                    LastFailure = expired ? CodexFailureCode.SessionExpired : _compatibility.LastFailure is
                        CodexFailureCode.SessionMissing or CodexFailureCode.SessionUnreadable or CodexFailureCode.UnsupportedSession
                        or CodexFailureCode.SessionExpired ? CodexFailureCode.None : _compatibility.LastFailure
                };
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                var failure = ClassifyFailure(ex, readingSession: true);
                warning = SessionWarning(failure);
                _compatibility = _compatibility with { Session = SessionStatus(failure), LastFailure = failure,
                    CheckedAt = DateTimeOffset.UtcNow };
            }
            var key = identity is null ? null : identity.Email.ToUpperInvariant() + "\n" + identity.AccountId;
            if (key == _activeKey)
            {
                if (key is null) Set(State with { StatusMessage = warning, SelectedAccountId = null });
                return;
            }
            _identityLifetime.Cancel();
            _identityLifetime.Dispose();
            _identityLifetime = new();
            _generation++;
            _notificationBaselineGeneration = -1;
            _activeObservation = null;
            _activeKey = key;
            _accountId = identity?.AccountId;
            _refresh = null;
            var accounts = State.Accounts.Select(a => a with { IsActiveInCodex = false, IsRefreshing = false }).ToList();
            Guid? selected = null;
            if (identity is not null)
            {
                var index = accounts.FindIndex(a => SameEmail(a.Profile.Email, identity.Email));
                if (index < 0)
                {
                    accounts.Add(new(new(Guid.NewGuid(), identity.Email)));
                    index = accounts.Count - 1;
                }
                accounts[index] = accounts[index] with { IsActiveInCodex = true, IsConnected = true, Error = null };
                selected = accounts[index].Profile.Id;
                _activeObservation = new(selected.Value, DateTimeOffset.UtcNow);
            }
            Set(State with { Accounts = accounts.ToArray(), SelectedAccountId = selected, IsBusy = false,
                StatusMessage = identity is null ? warning : "Compte Codex détecté · lecture des quotas…" });
            Save();
        }
        finally { _gate.Release(); }
    }

    private async Task<Task> QueueRefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed || _suspended) return Task.CompletedTask;
            PruneOldHistory(DateTimeOffset.UtcNow);
            if (_activeKey is null || _accountId is null) return Task.CompletedTask;
            if (_refresh is { IsCompleted: false }) return _refresh;
            _lastRefreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var active = State.Accounts.Single(a => a.IsActiveInCodex);
            var sequence = ++_requestSequence;
            _refresh = RefreshCoreAsync(active.Profile, _accountId, _generation, _identityLifetime.Token);
            _requests[sequence] = _refresh;
            _ = _refresh.ContinueWith(completed => _requests.TryRemove(sequence, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _refresh;
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshCoreAsync(AccountProfile profile, string accountId, long generation, CancellationToken identityToken)
    {
        await Task.Yield();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, identityToken);
        request.CancelAfter(_options.RequestTimeout);
        AccountSnapshot? snapshot = null;
        string? error = null;
        var failure = CodexFailureCode.None;
        IReadOnlyList<QuotaNotification> notifications = Array.Empty<QuotaNotification>();
        try
        {
            await _gate.WaitAsync(request.Token);
            try
            {
                if (generation != _generation) return;
                Update(profile.Id, a => a with { IsRefreshing = true });
                Set(State with { IsBusy = true, StatusMessage = "Actualisation du compte ouvert dans Codex…" });
            }
            finally { _gate.Release(); }
            var candidate = await _reader.ReadAsync(profile, accountId, request.Token);
            if (!SameEmail(candidate.Email, profile.Email)) throw new TrackerException("Codex a retourné un autre compte. Le relevé a été ignoré.", CodexFailureCode.AccountChanged);
            snapshot = candidate;
        }
        catch (OperationCanceledException)
        {
            if (_lifetime.IsCancellationRequested || identityToken.IsCancellationRequested) return;
            error = "Codex met trop de temps à répondre. Dernier relevé conservé.";
            failure = CodexFailureCode.RequestTimedOut;
        }
        catch (Exception ex) when (IsRecoverable(ex)) { error = SafeMessage(ex); failure = ClassifyFailure(ex); }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    // Re-read even if the watcher has not delivered an atomic replacement yet.
                    await DetectAsync(_lifetime.Token);
                    await _gate.WaitAsync(_lifetime.Token);
                    try
                    {
                        if (generation == _generation)
                        {
                            var previousSnapshot = State.Accounts.FirstOrDefault(a => a.Profile.Id == profile.Id)?.Snapshot;
                            if (snapshot is not null && previousSnapshot is not null && snapshot.FetchedAt < previousSnapshot.FetchedAt)
                            {
                                snapshot = null;
                                error = "Un relevé plus ancien a été ignoré. Les dernières données sont conservées.";
                                failure = CodexFailureCode.Unknown;
                            }
                            if (snapshot is not null) notifications = RecordObservation(profile, snapshot, generation);
                            _compatibility = _compatibility with
                            {
                                Service = snapshot is not null ? CodexServiceStatus.Compatible : failure switch
                                {
                                    CodexFailureCode.CodexNotFound => CodexServiceStatus.Missing,
                                    CodexFailureCode.ProtocolUnsupported => CodexServiceStatus.Incompatible,
                                    CodexFailureCode.SessionExpired => _compatibility.Service,
                                    _ => CodexServiceStatus.Unavailable
                                },
                                LastFailure = failure,
                                LastSuccessfulReadAt = snapshot?.FetchedAt ?? _compatibility.LastSuccessfulReadAt,
                                CheckedAt = DateTimeOffset.UtcNow
                            };
                            Update(profile.Id, a => a with { Snapshot = snapshot ?? a.Snapshot, IsRefreshing = false, Error = error });
                            Set(State with { IsBusy = false, StatusMessage = error ?? "À jour · changements de compte détectés automatiquement" });
                            Save();
                        }
                    }
                    finally { _gate.Release(); }
                    if (generation != _generation) _ = await QueueRefreshAsync(_lifetime.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (IsRecoverable(ex)) { /* Retain the in-memory state if disk becomes unavailable. */ }
            }
            foreach (var notification in notifications)
            {
                if (generation != _generation || _suspended || _disposed) break;
                Notification?.Invoke(this, notification);
            }
        }
    }

    private IReadOnlyList<QuotaNotification> RecordObservation(AccountProfile profile, AccountSnapshot snapshot, long generation)
    {
        var previous = _telemetry.GetValueOrDefault(profile.Id) ?? AccountTelemetry.Empty;
        var samples = UsageAnalytics.Append(previous.Samples, UsageAnalytics.FromSnapshot(profile.Id, snapshot));
        var evaluation = QuotaAlertEvaluator.Observe(profile, snapshot, previous.Alerts,
            suppressNotifications: _notificationBaselineGeneration != generation);
        _notificationBaselineGeneration = generation;
        var current = new AccountTelemetry(samples, evaluation.State);
        _telemetry[profile.Id] = current;
        try
        {
            // Persist deduplication before publishing events: restarting cannot replay an alert.
            _store.SaveTelemetry(profile.Id, current);
            return evaluation.Notifications;
        }
        catch (Exception ex) when (IsRecoverable(ex)) { return Array.Empty<QuotaNotification>(); }
    }

    private void PruneOldHistory(DateTimeOffset now)
    {
        var cutoff = now - UsageAnalytics.Retention;
        foreach (var (id, telemetry) in _telemetry)
        {
            if (telemetry.Samples.Count == 0 || telemetry.Samples[0].Timestamp >= cutoff) continue;
            var pruned = telemetry with { Samples = telemetry.Samples.Where(s => s.Timestamp >= cutoff).ToList().AsReadOnly() };
            _telemetry[id] = pruned;
            try { _store.SaveTelemetry(id, pruned); }
            catch (Exception ex) when (IsRecoverable(ex)) { }
        }
    }

    public async Task RemoveAccountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await ChangeAsync(() =>
        {
            if (State.Accounts.Any(a => a.Profile.Id == id && a.IsActiveInCodex))
                throw new TrackerException("Le compte actif est suivi automatiquement. Changez de compte dans Codex avant de le retirer.");
            _store.DeleteTelemetry(id);
            _telemetry.TryRemove(id, out _);
            Set(State with { Accounts = State.Accounts.Where(a => a.Profile.Id != id).ToArray(),
                SelectedAccountId = State.SelectedAccountId == id ? State.Accounts.FirstOrDefault(a => a.IsActiveInCodex)?.Profile.Id : State.SelectedAccountId });
        }, cancellationToken);
    }

    public Task CompleteOnboardingAsync(CancellationToken cancellationToken = default) =>
        ChangeAsync(() => Set(State with { OnboardingComplete = true }), cancellationToken);

    private async Task ChangeAsync(Action change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { RequireInitialized(); change(); Save(); }
        finally { _gate.Release(); }
    }

    private async Task RunTimerAsync()
    {
        using var timer = new PeriodicTimer(_options.RefreshIntervalProvider is null ? _options.RefreshInterval : TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                if (_options.RefreshIntervalProvider is { } interval &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastRefreshStarted)) < interval()) continue;
                try { await RefreshAsync(_lifetime.Token); }
                catch (Exception ex) when (IsRecoverable(ex)) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Save()
    {
        _store.SaveSettings(new(State.Accounts.Select(a => a.Profile).ToList(), State.SelectedAccountId,
            State.OnboardingComplete, State.Accounts.Where(a => a.IsConnected).Select(a => a.Profile.Id).ToList()));
        _store.SaveSnapshots(State.Accounts.Where(a => a.Snapshot is not null).ToDictionary(a => a.Profile.Id, a => a.Snapshot!));
    }
    private void Update(Guid id, Func<AccountState, AccountState> update) => Set(State with
    { Accounts = State.Accounts.Select(a => a.Profile.Id == id ? update(a) : a).ToArray() });
    private void Set(TrackerState state) { State = state; Changed?.Invoke(this, EventArgs.Empty); }
    private void RequireInitialized()
    {
        if (_disposed) throw new OperationCanceledException("Codex Tracker se ferme.");
        if (!_initialized) throw new TrackerException("Le tracker n'est pas encore prêt.");
    }
    private static bool SameEmail(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool IsRecoverable(Exception ex) => ex is TrackerException or IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception;
    private static string SafeMessage(Exception ex) => ex is TrackerException ? ex.Message : "Les données ne sont pas disponibles. Dernier relevé conservé ; réessayez depuis Codex.";
    private static CodexFailureCode ClassifyFailure(Exception ex, bool readingSession = false) => ex switch
    {
        TrackerException tracker => tracker.Code,
        FileNotFoundException or DirectoryNotFoundException when readingSession => CodexFailureCode.SessionMissing,
        UnauthorizedAccessException or IOException when readingSession => CodexFailureCode.SessionUnreadable,
        JsonException when readingSession => CodexFailureCode.UnsupportedSession,
        _ => CodexFailureCode.ServiceUnavailable
    };
    private static CodexSessionStatus SessionStatus(CodexFailureCode failure) => failure switch
    {
        CodexFailureCode.SessionMissing => CodexSessionStatus.Missing,
        CodexFailureCode.SessionUnreadable => CodexSessionStatus.Unreadable,
        CodexFailureCode.InvalidHome => CodexSessionStatus.InvalidHome,
        _ => CodexSessionStatus.Unsupported
    };
    private static string SessionWarning(CodexFailureCode failure) => failure switch
    {
        CodexFailureCode.SessionMissing => "Aucune session locale détectée. Ouvrez Codex et connectez-vous avec votre compte ChatGPT.",
        CodexFailureCode.SessionUnreadable => "La session Codex est momentanément illisible. Fermez puis rouvrez Codex et actualisez le tracker.",
        CodexFailureCode.InvalidHome => "Le dossier CODEX_HOME est invalide. Corrigez cette variable puis relancez le tracker.",
        _ => "Le format de la session n'est pas pris en charge. Mettez à jour Codex et connectez-vous avec votre compte ChatGPT ; une clé API seule ne permet pas de suivre ces quotas."
    };

    public async ValueTask DisposeAsync()
    {
        Task[] requests;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            requests = _requests.Values.ToArray();
        }
        finally { _gate.Release(); }
        _lifetime.Cancel();
        if (_monitor is not null) await _monitor.DisposeAsync();
        if (_timer is not null) await _timer;
        await Task.WhenAll(requests);
        _identityLifetime.Dispose();
        _lifetime.Dispose();
        _store.Dispose();
        // Pending UI commands may still enter and observe _disposed. SemaphoreSlim owns no
        // native handle unless its AvailableWaitHandle is requested (it never is here).
    }
}
