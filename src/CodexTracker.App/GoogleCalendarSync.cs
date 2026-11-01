using System.Diagnostics;
using System.Net.Http;
using CodexTracker.Codex;

namespace CodexTracker.App;

/// <summary>
/// Keeps one Google Calendar event per reset credit. Runs on the WPF dispatcher; calls are serialized.
/// Without a connection, a credit can still be opened prefilled in the browser (unverifiable).
/// </summary>
internal sealed class GoogleCalendarSync : IDisposable
{
    private static readonly TimeSpan Verification = TimeSpan.FromMinutes(30), Retry = TimeSpan.FromMinutes(15);
    private readonly HttpClient _http = GoogleCalendarClient.CreateHttpClient();
    private readonly GoogleCalendarClient _client;
    private readonly GoogleCalendarSecretStore? _secrets;
    private readonly CalendarLinkStore _links;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action<Uri> _openBrowser;
    private GoogleCalendarSecrets _current = new();
    private DateTimeOffset _nextVerification, _retryAfter;
    private bool _disposed;
    internal event EventHandler? Changed;
    /// <summary>Last synchronization problem, without any token or address.</summary>
    internal string? Error { get; private set; }
    internal bool IsAvailable { get; }
    internal bool IsConnected => _current.IsConnected;
    internal bool HasClient => _current.Client is { IsValid: true };
    internal string? Email => _current.Email;
    internal bool IsConnecting { get; private set; }

    internal GoogleCalendarSync(string dataDirectory, bool demo, Action<Uri>? openBrowser = null)
    {
        _client = new(_http);
        _openBrowser = openBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        IsAvailable = !demo;
        try { _links = new(demo ? null : dataDirectory); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Never overwrite an unreadable file: statuses stay unknown until it is repaired.
            _links = new(null); IsAvailable = false; Error = Loc.T("Le suivi des événements d’agenda est illisible. L’ajout automatique est suspendu.");
        }
        if (demo || !IsAvailable) return;
        _secrets = new(dataDirectory);
        try { _current = _secrets.Read(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        { Error = Loc.T("Connexion Google Agenda illisible. Reconnectez Google Agenda."); }
    }

    internal static string Key(Guid accountId, string creditId, DateTimeOffset expiresAt) => CalendarExport.Identity(accountId, "credit", creditId, expiresAt);
    internal CalendarLink? Link(string key) => _links.Get(key);

    internal void ImportClient(string json)
    {
        Available();
        var client = GoogleOAuthClient.Parse(json);
        // A different client cannot use the previous refresh token.
        Save(_current.Client == client ? _current : new(client));
    }

    internal async Task ConnectAsync(CancellationToken token = default)
    {
        Available();
        if (_current.Client is not { IsValid: true } client) throw new InvalidOperationException(Loc.T("Importez d’abord le fichier du client OAuth Google."));
        if (IsConnecting) return;
        IsConnecting = true; Notify();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var receiver = new GoogleLoopbackReceiver();
            var verifier = GoogleCalendarClient.RandomToken() + GoogleCalendarClient.RandomToken();
            var state = GoogleCalendarClient.RandomToken();
            _openBrowser(GoogleCalendarClient.AuthorizationUri(client, receiver.RedirectUri, GoogleCalendarClient.Challenge(verifier), state));
            string code;
            try { code = await receiver.ReceiveCodeAsync(state, timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            { throw new GoogleCalendarException(Loc.T("Connexion non terminée dans les 5 minutes. Réessayez.")); }
            var (refresh, email) = await _client.ExchangeAsync(client, code, verifier, receiver.RedirectUri, timeout.Token);
            Save(new(client, refresh, email));
            Error = null; _retryAfter = default; _nextVerification = default;
        }
        finally { IsConnecting = false; Notify(); }
    }

    internal async Task DisconnectAsync()
    {
        if (_current.RefreshToken is { } refresh) await _client.RevokeAsync(refresh, _lifetime.Token);
        if (_secrets is not null) Save(_current with { RefreshToken = null, Email = null });
        Error = null; Notify();
    }

    internal void ForgetClient()
    {
        _secrets?.Delete(); _current = new(); Error = null; Notify();
    }

    /// <summary>Adds the credit's expiry, with reminders, to the primary Google calendar. Idempotent.</summary>
    internal async Task<CalendarLink> AddAsync(CalendarEntry entry)
    {
        Available();
        await _gate.WaitAsync(_lifetime.Token);
        try { return await InsertAsync(entry); }
        finally { _gate.Release(); }
    }

    /// <summary>Opens the prefilled event: the user saves it in Google. The status stays "not verified".</summary>
    internal CalendarLink OpenInBrowser(CalendarEntry entry)
    {
        _openBrowser(CalendarExport.GoogleEventLink(entry));
        var link = new CalendarLink(CalendarExport.Key(entry), CalendarLinkSource.Browser, CalendarLinkStatus.Opened, entry.StartsAt, DateTimeOffset.UtcNow);
        if (IsAvailable && _links.Get(link.Key) is not { Status: CalendarLinkStatus.Confirmed }) { _links.Put(link); Notify(); }
        return _links.Get(link.Key) ?? link;
    }

    internal void Forget(string key) { if (IsAvailable) { _links.Remove(key); Notify(); } }

    /// <summary>
    /// Adds the credits that have no event yet (never one the user deleted or opened in the browser),
    /// then checks from time to time that the confirmed events still exist.
    /// </summary>
    internal async Task SyncAsync(TrackerState state, bool autoAdd, Func<AccountProfile, string> name)
    {
        if (!IsAvailable || !IsConnected || _disposed || DateTimeOffset.UtcNow < _retryAfter || !await _gate.WaitAsync(0)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            _links.Prune(now);
            if (autoAdd)
                foreach (var entry in Credits(state, now, name).Where(e => _links.Get(CalendarExport.Key(e)) is null).Take(10))
                    await InsertAsync(entry);
            if (now >= _nextVerification)
            {
                foreach (var link in _links.Links.Values.Where(l => l is { Source: CalendarLinkSource.Google, Status: CalendarLinkStatus.Confirmed } && l.At > now).Take(25).ToArray())
                {
                    var found = await _client.GetAsync(_current, link.Key, _lifetime.Token);
                    _links.Put(found is null || found.IsCancelled
                        ? link with { Status = CalendarLinkStatus.Deleted, UpdatedAt = DateTimeOffset.UtcNow }
                        : link with { HtmlLink = found.HtmlLink ?? link.HtmlLink, VerifiedAt = DateTimeOffset.UtcNow });
                }
                _nextVerification = DateTimeOffset.UtcNow + Verification;
            }
            if (Error is not null) { Error = null; Notify(); }
        }
        catch (GoogleCalendarException ex) { Error = ex.Message; _retryAfter = DateTimeOffset.UtcNow + Retry; Notify(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = Loc.T("Impossible d’enregistrer le suivi des événements d’agenda."); _retryAfter = DateTimeOffset.UtcNow + Retry; Notify(); }
        finally { _gate.Release(); }
    }

    /// <summary>Credits still held at the last observation and not expired, as calendar entries.</summary>
    internal static IEnumerable<CalendarEntry> Credits(TrackerState state, DateTimeOffset now, Func<AccountProfile, string> name) => state.Accounts
        .Where(a => a.Profile.Provider == AccountProvider.Codex)
        .SelectMany(a => (a.Snapshot?.ResetCredits ?? []).Select(c => CalendarExport.CreditEntry(a, c.Id, now, name)))
        .OfType<CalendarEntry>();

    private async Task<CalendarLink> InsertAsync(CalendarEntry entry)
    {
        var created = await _client.InsertAsync(_current, entry, _lifetime.Token);
        var link = new CalendarLink(CalendarExport.Key(entry), CalendarLinkSource.Google,
            created.IsCancelled ? CalendarLinkStatus.Deleted : CalendarLinkStatus.Confirmed, entry.StartsAt, DateTimeOffset.UtcNow, created.HtmlLink, DateTimeOffset.UtcNow);
        _links.Put(link); Notify();
        return link;
    }

    private void Save(GoogleCalendarSecrets secrets)
    {
        _secrets!.Save(secrets); _current = secrets; Notify();
    }
    private void Available()
    {
        if (!IsAvailable) throw new InvalidOperationException(Error ?? Loc.T("Google Agenda n’est pas disponible dans la démonstration."));
    }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _http.Dispose();
    }
}
