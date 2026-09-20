using System.Security.Cryptography;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed record TrackerServiceOptions
{
    public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker");
    public string? CodexExecutablePath { get; init; }
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(35);
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool AutomaticRefresh { get; init; } = true;
}

public sealed class TrackerService : ITrackerService
{
    private readonly IDesktopSessionManager _desktop;
    private readonly TrackerServiceOptions _options;
    private readonly ProfileStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _timer;
    private bool _initialized;
    private string? _executable;
    public event EventHandler? Changed;
    public TrackerState State { get; private set; } = new([], null, StatusMessage: "Préparation de Codex Tracker…");

    public TrackerService() : this(new DesktopSessionManager()) { }
    public TrackerService(IDesktopSessionManager desktop, TrackerServiceOptions? options = null)
    {
        _desktop = desktop;
        _options = options ?? new();
        _store = new(_options.DataDirectory);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            _store.Open();
            var settings = _store.LoadSettings();
            var snapshots = _store.LoadSnapshots();
            _executable = _options.CodexExecutablePath ?? CodexLocator.FindExecutable();
            var availability = await _desktop.GetAvailabilityAsync(cancellationToken);
            Set(State with
            {
                Accounts = settings.Accounts.Select(profile => new AccountState(profile, snapshots.GetValueOrDefault(profile.Id), IsConnected: _store.HasAuth(profile.Id))).ToArray(),
                SelectedAccountId = settings.SelectedAccountId ?? settings.Accounts.FirstOrDefault()?.Id,
                OnboardingComplete = settings.OnboardingComplete,
                CanSwitch = availability.CanSwitch,
                SwitchUnavailableReason = availability.Reason,
                PendingSwitchEmail = _desktop.PendingTargetEmail,
                StatusMessage = _executable is null ? "Installez ou ouvrez Codex pour activer le suivi." : "Prêt à actualiser"
            });
            _initialized = true;
            try { await ImportCurrentCoreAsync(cancellationToken); }
            catch (Exception ex) when (IsRecoverable(ex)) { Set(State with { StatusMessage = SafeMessage(ex) }); }
            if (State.SelectedAccount is not { IsConnected: true } && State.Accounts.FirstOrDefault(a => a.IsActiveInCodex) is { } active)
                Set(State with { SelectedAccountId = active.Profile.Id });
            Save();
        }
        finally { _gate.Release(); }
        await RefreshAsync(cancellationToken);
        if (_options.AutomaticRefresh && _timer is null) _timer = RunTimerAsync();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try
        {
            RequireInitialized();
            Set(State with { IsBusy = true, StatusMessage = "Actualisation des comptes…" });
            var availability = await _desktop.GetAvailabilityAsync(linked.Token);
            Set(State with { CanSwitch = availability.CanSwitch, SwitchUnavailableReason = availability.Reason, PendingSwitchEmail = _desktop.PendingTargetEmail });
            AuthDocument? desktopIdentity = null;
            try
            {
                if (File.Exists(_desktop.AuthFilePath))
                {
                    var bytes = await ReadDesktopAuthAsync(linked.Token);
                    try { desktopIdentity = AuthDocument.Parse(bytes); await ImportBytesCoreAsync(bytes, linked.Token); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
            }
            catch (Exception ex) when (IsRecoverable(ex)) { Set(State with { StatusMessage = SafeMessage(ex) }); }
            MarkActive(desktopIdentity?.Email);
            foreach (var account in State.Accounts.ToArray())
            {
                linked.Token.ThrowIfCancellationRequested();
                if (!account.IsConnected) continue;
                UpdateAccount(account.Profile.Id, a => a with { IsRefreshing = true });
                try
                {
                    if (_executable is null) throw new TrackerException("Le programme Codex est introuvable. Installez Codex puis redémarrez le tracker.");
                    // Unknown desktop identity must never let a collector take ownership of its refresh token.
                    if (availability.IsRunning && desktopIdentity is null)
                        throw new TrackerException("La session active de Codex est illisible. Reconnectez-vous dans Codex avant d'actualiser.");
                    var isDesktopOwned = SameEmail(account.Profile.Email, desktopIdentity?.Email);
                    // The pending rollback journal owns the previous refresh token until confirmation.
                    // Refreshing any inactive profile here could invalidate that durable rollback copy.
                    if (_desktop.PendingTargetEmail is not null && !isDesktopOwned)
                    {
                        UpdateAccount(account.Profile.Id, a => a with { IsRefreshing = false });
                        continue;
                    }
                    var snapshot = await FetchSnapshotAsync(account.Profile, isDesktopOwned, linked.Token);
                    UpdateAccount(account.Profile.Id, a => a with { Snapshot = snapshot, Error = null, IsRefreshing = false });
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsRecoverable(ex))
                { UpdateAccount(account.Profile.Id, a => a with { Error = SafeMessage(ex), IsRefreshing = false }); }
            }
            Save();
            var failures = State.Accounts.Count(a => a.Error is not null);
            Set(State with { StatusMessage = failures == 0 ? "Données à jour" : $"{failures} compte(s) à vérifier · dernières données conservées" });
        }
        finally
        {
            Set(State with { IsBusy = false, Accounts = State.Accounts.Select(a => a with { IsRefreshing = false }).ToArray() });
            _gate.Release();
        }
    }

    private async Task<AccountSnapshot> FetchSnapshotAsync(AccountProfile profile, bool desktopOwned, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        var auth = desktopOwned ? await ReadDesktopAuthAsync(timeout.Token) : _store.LoadAuth(profile.Id);
        string? runtime = null;
        AppServerClient? client = null;
        try
        {
            var identity = AuthDocument.Parse(auth);
            ValidateEmail(profile.Email, identity.Email);
            runtime = _store.CreateRuntime(desktopOwned ? Guid.NewGuid() : profile.Id, desktopOwned ? null : auth);
            await using var rpc = await AppServerClient.StartAsync(_executable!, runtime, desktopOwned,
                desktopOwned ? ct => RereadTokensAsync(profile.Email, identity.AccountId, ct) : null, timeout.Token);
            client = rpc;
            if (desktopOwned) await rpc.RequestAsync("account/login/start", Tokens(identity), timeout.Token);
            var response = await rpc.RequestAsync("account/read", new { refreshToken = false }, timeout.Token);
            var account = GetAccount(response, profile.Email);
            var quotas = await rpc.RequestAsync("account/rateLimits/read", new { }, timeout.Token);
            if (AuthDocument.Read(quotas, "accountId") is { } quotaAccount && quotaAccount != identity.AccountId)
                throw new TrackerException("Codex a renvoyé les quotas d'un autre espace de travail. Aucune donnée n'a été enregistrée.");
            return RateLimitParser.Parse(quotas, profile.Email, account.PlanType, DateTimeOffset.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(auth);
            if (runtime is not null && (client is null || client.IsStopped))
            {
                // app-server is disposed before this finally: persisted rotations are now stable.
                if (!desktopOwned)
                {
                    // If encryption/storage fails, retain this private directory for crash recovery.
                    // Deleting it would discard the only usable rotated refresh token.
                    SaveRuntimeAuth(profile, runtime);
                }
                _store.DeleteRuntime(runtime);
            }
        }
    }

    public async Task AddAccountAsync(string email, CancellationToken cancellationToken = default)
    {
        email = email.Trim();
        if (!ProfileStore.IsEmail(email)) throw new TrackerException("Saisissez une adresse e-mail complète et valide.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireInitialized();
            if (State.Accounts.Any(a => SameEmail(a.Profile.Email, email))) throw new TrackerException("Ce compte figure déjà dans votre liste.");
            var account = new AccountState(new(Guid.NewGuid(), email));
            Set(State with { Accounts = [.. State.Accounts, account], SelectedAccountId = State.SelectedAccountId ?? account.Profile.Id });
            Save();
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAccountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireInitialized();
            var account = Find(id);
            if (SameEmail(account.Profile.Email, _desktop.PendingTargetEmail)) throw new TrackerException("Terminez la vérification du changement de compte avant de le supprimer.");
            _store.DeleteAuth(id);
            var accounts = State.Accounts.Where(a => a.Profile.Id != id).ToArray();
            Set(State with { Accounts = accounts, SelectedAccountId = State.SelectedAccountId == id ? accounts.FirstOrDefault()?.Profile.Id : State.SelectedAccountId });
            Save();
        }
        finally { _gate.Release(); }
    }

    public async Task SelectAccountAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { RequireInitialized(); Find(id); Set(State with { SelectedAccountId = id }); Save(); }
        finally { _gate.Release(); }
    }

    public async Task CompleteOnboardingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { RequireInitialized(); Set(State with { OnboardingComplete = true }); Save(); }
        finally { _gate.Release(); }
    }

    public async Task ConnectAccountAsync(Guid id, Action<LoginPrompt> onLogin, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(_options.LoginTimeout);
        await _gate.WaitAsync(timeout.Token);
        string? runtime = null;
        try
        {
            RequireInitialized();
            var profile = Find(id).Profile;
            _executable ??= _options.CodexExecutablePath ?? CodexLocator.FindExecutable();
            if (_executable is null) throw new TrackerException("Installez Codex pour connecter vos comptes.");
            Set(State with { IsBusy = true, StatusMessage = "Connexion dans le navigateur…" });
            // A new random runtime cannot overwrite a saved session during crash recovery before email verification.
            runtime = _store.CreateRuntime(Guid.NewGuid(), null);
            await using var rpc = await AppServerClient.StartAsync(_executable, runtime, false, null, timeout.Token);
            string? loginId = null;
            try
            {
                var start = await rpc.RequestAsync("account/login/start", new { type = "chatgpt" }, timeout.Token);
                loginId = AuthDocument.Read(start, "loginId") ?? throw new TrackerException("La connexion OAuth n'a pas pu démarrer.");
                var url = AuthDocument.Read(start, "authUrl");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new TrackerException("Codex a renvoyé une adresse de connexion non valide.");
                onLogin(new(uri));
                var completed = await rpc.WaitForLoginAsync(loginId, timeout.Token);
                if (!completed.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                    throw new TrackerException("La connexion a échoué ou a été annulée. Réessayez depuis le navigateur.");
                var read = await rpc.RequestAsync("account/read", new { refreshToken = false }, timeout.Token);
                GetAccount(read, profile.Email);
                var auth = await File.ReadAllBytesAsync(Path.Combine(runtime, "auth.json"), timeout.Token);
                try { ValidateEmail(profile.Email, AuthDocument.Parse(auth).Email); _store.SaveAuth(profile.Id, auth); }
                finally { CryptographicOperations.ZeroMemory(auth); }
                UpdateAccount(id, a => a with { IsConnected = true, Error = null });
                Save();
                Set(State with { StatusMessage = "Compte connecté. Actualisation disponible." });
            }
            catch
            {
                if (loginId is not null)
                {
                    using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await rpc.RequestAsync("account/login/cancel", new { loginId }, cancelTimeout.Token); }
                    catch (Exception ex) when (IsRecoverable(ex)) { }
                }
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        { throw new TrackerException("Le délai de connexion a expiré. Réessayez pour ouvrir un nouveau lien."); }
        finally
        {
            try { if (runtime is not null) _store.DeleteRuntime(runtime); }
            finally { Set(State with { IsBusy = false }); _gate.Release(); }
        }
        await RefreshAsync(cancellationToken);
    }

    public async Task ImportCurrentAccountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { RequireInitialized(); await ImportCurrentCoreAsync(cancellationToken); Save(); }
        finally { _gate.Release(); }
        await RefreshAsync(cancellationToken);
    }

    private async Task ImportCurrentCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_desktop.AuthFilePath)) { MarkActive(null); return; }
        var auth = await ReadDesktopAuthAsync(cancellationToken);
        try { await ImportBytesCoreAsync(auth, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(auth); }
    }

    private Task ImportBytesCoreAsync(byte[] auth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = AuthDocument.Parse(auth);
        var existing = State.Accounts.FirstOrDefault(a => SameEmail(a.Profile.Email, identity.Email));
        if (existing is null)
        {
            existing = new(new(Guid.NewGuid(), identity.Email));
            Set(State with { Accounts = [.. State.Accounts, existing], SelectedAccountId = State.SelectedAccountId ?? existing.Profile.Id });
        }
        _store.SaveAuth(existing.Profile.Id, auth);
        UpdateAccount(existing.Profile.Id, a => a with { IsConnected = true });
        MarkActive(identity.Email);
        return Task.CompletedTask;
    }

    public async Task<SwitchResult> SwitchAccountAsync(Guid id, bool confirmed, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireInitialized();
            var account = Find(id);
            if (!confirmed) return new(false, "Changement de compte annulé.");
            if (!account.IsConnected) return new(false, "Connectez ce compte avant de l'utiliser dans Codex.");
            Set(State with { IsBusy = true, StatusMessage = "Changement de compte…" });
            await ImportCurrentCoreAsync(cancellationToken);
            // Active status may have changed in Codex since the last periodic refresh.
            if (Find(id).IsActiveInCodex && _desktop.PendingTargetEmail is null)
                return new(false, "Ce compte est déjà configuré dans le cache de Codex. Vérifiez son adresse dans le menu de compte de Codex.");
            var target = _store.LoadAuth(id);
            try
            {
                ValidateEmail(account.Profile.Email, AuthDocument.Parse(target).Email);
                var result = await _desktop.ActivateAsync(target, account.Profile.Email, confirmed, cancellationToken);
                var captureWarning = await CaptureTransitionAsync(result);
                var reloadWarning = await ReloadAfterTransitionAsync();
                var message = result.Message + captureWarning + reloadWarning;
                Set(State with { PendingSwitchEmail = _desktop.PendingTargetEmail, StatusMessage = message });
                Save();
                return new(result.Success, message, result.NeedsUserVerification);
            }
            finally { CryptographicOperations.ZeroMemory(target); }
        }
        finally { Set(State with { IsBusy = false }); _gate.Release(); }
    }

    public async Task<SwitchResult> ConfirmSwitchAsync(bool accepted, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RequireInitialized();
            Set(State with { IsBusy = true });
            // Save any target rotation before the manager restores the old desktop cache.
            try { await ImportCurrentCoreAsync(cancellationToken); }
            catch (Exception ex) when (!accepted && IsRecoverable(ex))
            {
                // A missing/corrupt current cache is precisely when the valid rollback journal is needed.
                // The manager captures any remaining target credentials after closing the desktop.
            }
            var result = await _desktop.ConfirmActivationAsync(accepted, cancellationToken);
            var captureWarning = await CaptureTransitionAsync(result);
            var reloadWarning = await ReloadAfterTransitionAsync();
            var message = result.Message + captureWarning + reloadWarning;
            Set(State with { PendingSwitchEmail = _desktop.PendingTargetEmail, StatusMessage = message });
            Save();
            return new(result.Success, message, result.NeedsUserVerification);
        }
        finally { Set(State with { IsBusy = false }); _gate.Release(); }
    }

    private async Task<bool> CapturePreviousAsync(byte[]? auth)
    {
        if (auth is null) return true;
        try { await ImportBytesCoreAsync(auth, CancellationToken.None); return true; }
        catch (Exception ex) when (IsRecoverable(ex)) { return false; }
        finally { CryptographicOperations.ZeroMemory(auth); }
    }

    private async Task<string> CaptureTransitionAsync(DesktopActivationResult result)
    {
        var previousSaved = await CapturePreviousAsync(result.PreviousAuthJson);
        var displacedSaved = await CapturePreviousAsync(result.DisplacedAuthJson);
        return previousSaved && displacedSaved ? "" : " Une session sortante n'a pas pu être conservée ; reconnectez ce compte si son suivi échoue.";
    }

    private async Task<string> ReloadAfterTransitionAsync()
    {
        try
        {
            await ImportCurrentCoreAsync(CancellationToken.None);
            var availability = await _desktop.GetAvailabilityAsync(CancellationToken.None);
            Set(State with { CanSwitch = availability.CanSwitch, SwitchUnavailableReason = availability.Reason });
            return "";
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            MarkActive(null);
            return " Le tracker n'a pas pu relire la session ; actualisez après avoir vérifié Codex.";
        }
    }

    private async Task<byte[]> ReadDesktopAuthAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await File.ReadAllBytesAsync(_desktop.AuthFilePath, cancellationToken); }
            catch (IOException) when (attempt < 2) { await Task.Delay(80, cancellationToken); }
        }
    }

    private async Task<object> RereadTokensAsync(string email, string accountId, CancellationToken cancellationToken)
    {
        var auth = await ReadDesktopAuthAsync(cancellationToken);
        try
        {
            var identity = AuthDocument.Parse(auth);
            ValidateEmail(email, identity.Email);
            if (identity.AccountId != accountId) throw new TrackerException("L'espace de travail actif a changé.");
            // Re-reading is the only refresh path permitted for the session owned by the desktop.
            return new { accessToken = identity.AccessToken, chatgptAccountId = identity.AccountId, chatgptPlanType = identity.PlanType };
        }
        finally { CryptographicOperations.ZeroMemory(auth); }
    }

    private static object Tokens(AuthDocument identity) => new
    { type = "chatgptAuthTokens", accessToken = identity.AccessToken, chatgptAccountId = identity.AccountId, chatgptPlanType = identity.PlanType };

    private static (string Email, string? PlanType) GetAccount(JsonElement response, string expectedEmail)
    {
        if (!response.TryGetProperty("account", out var account) || AuthDocument.Read(account, "type") != "chatgpt")
            throw new TrackerException("La session ChatGPT a expiré. Reconnectez ce compte.");
        var email = AuthDocument.Read(account, "email") ?? throw new TrackerException("Codex n'a pas confirmé l'adresse du compte.");
        ValidateEmail(expectedEmail, email);
        return (email, AuthDocument.Read(account, "planType"));
    }

    private void SaveRuntimeAuth(AccountProfile profile, string runtime)
    {
        var path = Path.Combine(runtime, "auth.json");
        if (!File.Exists(path)) return;
        var auth = File.ReadAllBytes(path);
        try { ValidateEmail(profile.Email, AuthDocument.Parse(auth).Email); _store.SaveAuth(profile.Id, auth); }
        finally { CryptographicOperations.ZeroMemory(auth); }
    }

    private async Task RunTimerAsync()
    {
        using var timer = new PeriodicTimer(_options.RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                // A login or switch already owns the gate; skip this tick rather than building a queue.
                if (State.IsBusy) continue;
                try { await RefreshAsync(_lifetime.Token); }
                catch (Exception ex) when (IsRecoverable(ex) && !_lifetime.IsCancellationRequested) { Set(State with { StatusMessage = SafeMessage(ex) }); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Save()
    {
        _store.SaveSettings(new(State.Accounts.Select(a => a.Profile).ToList(), State.SelectedAccountId, State.OnboardingComplete));
        _store.SaveSnapshots(State.Accounts.Where(a => a.Snapshot is not null).ToDictionary(a => a.Profile.Id, a => a.Snapshot!));
    }
    private void MarkActive(string? email) => Set(State with { Accounts = State.Accounts.Select(a => a with { IsActiveInCodex = SameEmail(a.Profile.Email, email) }).ToArray() });
    private AccountState Find(Guid id) => State.Accounts.FirstOrDefault(a => a.Profile.Id == id) ?? throw new TrackerException("Ce compte n'existe plus dans le tracker.");
    private void UpdateAccount(Guid id, Func<AccountState, AccountState> change) => Set(State with { Accounts = State.Accounts.Select(a => a.Profile.Id == id ? change(a) : a).ToArray() });
    private void Set(TrackerState state) { State = state; Changed?.Invoke(this, EventArgs.Empty); }
    private void RequireInitialized() { if (!_initialized) throw new TrackerException("Le tracker n'est pas encore prêt."); }
    private static bool SameEmail(string? left, string? right) => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static void ValidateEmail(string expected, string actual)
    { if (!SameEmail(expected, actual)) throw new TrackerException("Le compte connecté ne correspond pas à l'adresse attendue. Choisissez le bon compte dans le navigateur."); }
    private static bool IsRecoverable(Exception ex) => ex is TrackerException or IOException or UnauthorizedAccessException or JsonException or CryptographicException or OperationCanceledException or System.ComponentModel.Win32Exception;
    private static string SafeMessage(Exception ex) => ex switch
    {
        TrackerException => ex.Message,
        OperationCanceledException => "Codex n'a pas répondu à temps. Les dernières données sont conservées.",
        UnauthorizedAccessException => "Accès aux données locales refusé. Vérifiez votre session Windows.",
        CryptographicException => "La session chiffrée est illisible. Reconnectez ce compte.",
        _ => "Le service Codex ou ses données locales sont indisponibles. Actualisez pour réessayer."
    };

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_timer is not null) await _timer;
        await _gate.WaitAsync();
        try { _store.Dispose(); }
        finally { _gate.Release(); _gate.Dispose(); _lifetime.Dispose(); }
    }
}
