using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexTracker.Core;

namespace CodexTracker.Codex;

/// <summary>A "Desktop app" OAuth client created by the user in Google Cloud. Google does not treat its secret as confidential.</summary>
/// <param name="ProjectId">Google Cloud project of the client, for direct links to its settings.</param>
public sealed record GoogleOAuthClient(string ClientId, string ClientSecret, string? ProjectId = null)
{
    /// <summary>Reads the JSON file downloaded from Google Cloud Console (Credentials → OAuth client → Desktop app).</summary>
    public static GoogleOAuthClient Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 8 });
            if (!document.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Object)
                throw new ArgumentException(Loc.T("Choisissez le fichier d’un client OAuth de type « Application de bureau »."));
            var id = installed.TryGetProperty("client_id", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
            var secret = installed.TryGetProperty("client_secret", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : "";
            var project = installed.TryGetProperty("project_id", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var client = new GoogleOAuthClient(id.Trim(), secret.Trim(),
                project is { Length: > 0 and <= 64 } && project.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') ? project : null);
            if (!client.IsValid) throw new ArgumentException(Loc.T("Fichier client OAuth incomplet."));
            return client;
        }
        catch (JsonException) { throw new ArgumentException(Loc.T("Fichier client OAuth illisible.")); }
    }
    /// <summary>The same client, whatever the optional metadata read with it.</summary>
    public bool SameAs(GoogleOAuthClient? other) => other is not null && other.ClientId == ClientId && other.ClientSecret == ClientSecret;
    [JsonIgnore] public bool IsValid => ClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) && ClientId.Length <= 256 &&
        ClientSecret.Length is > 0 and <= 256 && ClientId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') &&
        ClientSecret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public sealed record GoogleCalendarSecrets(GoogleOAuthClient? Client = null, string? RefreshToken = null, string? Email = null)
{
    [JsonIgnore] public bool IsConnected => Client is { IsValid: true } && !string.IsNullOrEmpty(RefreshToken);
}

/// <summary>DPAPI (current user), like the reminder channels. Never readable by the MCP bridge.</summary>
public sealed class GoogleCalendarSecretStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "google-calendar.dpapi");
    public GoogleCalendarSecrets Read()
    {
        if (!File.Exists(_path)) return new();
        var bytes = NotificationSecretStore.Protect(File.ReadAllBytes(_path), false);
        try { return JsonSerializer.Deserialize<GoogleCalendarSecrets>(bytes, NotificationFiles.Json) ?? throw new InvalidDataException("Identifiants illisibles."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Save(GoogleCalendarSecrets secrets)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(secrets, NotificationFiles.Json);
        try { NotificationFiles.Write(_path, NotificationSecretStore.Protect(bytes, true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Delete() { if (File.Exists(_path)) File.Delete(_path); }
}

public enum CalendarLinkSource { Google, Browser }
/// <summary>Confirmed: present in Google. Opened: prefilled in the browser, saving not verifiable. Deleted: removed in Google.</summary>
public enum CalendarLinkStatus { Confirmed, Opened, Deleted }
public sealed record CalendarLink(string Key, CalendarLinkSource Source, CalendarLinkStatus Status, DateTimeOffset At,
    DateTimeOffset UpdatedAt, string? HtmlLink = null, DateTimeOffset? VerifiedAt = null);

/// <summary>Which calendar event each credit has, keyed by its calendar identity. Owned by the WPF process.</summary>
public sealed class CalendarLinkStore
{
    private readonly string? _path;
    public IReadOnlyDictionary<string, CalendarLink> Links { get; private set; }
    public CalendarLinkStore(string? directory)
    {
        _path = directory is null ? null : Path.Combine(directory, "calendar-links.json");
        var links = _path is not null && File.Exists(_path)
            ? JsonSerializer.Deserialize<List<CalendarLink>>(File.ReadAllText(_path), NotificationFiles.Json) ?? throw new InvalidDataException("Liens d’agenda illisibles.") : [];
        if (links.Any(l => l is not { Key.Length: 64 } || !Enum.IsDefined(l.Source) || !Enum.IsDefined(l.Status))) throw new InvalidDataException("Liens d’agenda illisibles.");
        Links = links.ToDictionary(l => l.Key, StringComparer.Ordinal);
    }
    public CalendarLink? Get(string key) => Links.GetValueOrDefault(key);
    public void Put(CalendarLink link) => Save(Links.Values.Where(l => l.Key != link.Key).Append(link));
    public void Remove(string key) { if (Links.ContainsKey(key)) Save(Links.Values.Where(l => l.Key != key)); }
    /// <summary>Past events are no longer shown: keep them a month for the history, then forget them.</summary>
    public void Prune(DateTimeOffset now) { if (Links.Values.Any(l => l.At < now.AddDays(-30))) Save(Links.Values.Where(l => l.At >= now.AddDays(-30))); }
    private void Save(IEnumerable<CalendarLink> links)
    {
        var list = links.OrderBy(l => l.At).ToList();
        if (_path is not null) NotificationFiles.Write(_path, JsonSerializer.SerializeToUtf8Bytes(list, NotificationFiles.Json));
        Links = list.ToDictionary(l => l.Key, StringComparer.Ordinal);
    }
}

public enum GoogleCalendarProblem { Other, Reconnect, ApiDisabled, MissingScope, Unavailable }

public sealed class GoogleCalendarException(string message, bool reconnect = false, GoogleCalendarProblem problem = GoogleCalendarProblem.Other) : Exception(message)
{
    public bool RequiresReconnect { get; } = reconnect;
    public GoogleCalendarProblem Problem { get; } = reconnect && problem == GoogleCalendarProblem.Other ? GoogleCalendarProblem.Reconnect : problem;
}

public sealed record GoogleEvent(string Id, string Status, string? HtmlLink)
{
    public bool IsCancelled => Status == "cancelled";
}

/// <summary>
/// Google Calendar v3 on the primary calendar, limited to the events this app creates. Event ids derive from the
/// calendar identity, so a repeated request finds the existing event instead of creating a duplicate.
/// </summary>
public sealed class GoogleCalendarClient(HttpClient http)
{
    public const string Scope = "https://www.googleapis.com/auth/calendar.events openid email";
    private const string Events = "https://www.googleapis.com/calendar/v3/calendars/primary/events";
    /// <summary>Popups 3 days, 1 day and 1 hour before the credit expires.</summary>
    public static readonly int[] ReminderMinutes = [4320, 1440, 60];
    private string? _accessToken, _tokenFor;
    private DateTimeOffset _accessExpires;

    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
    { Timeout = TimeSpan.FromSeconds(20) };

    public static Uri AuthorizationUri(GoogleOAuthClient client, Uri redirect, string challenge, string state) => new(
        "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&access_type=offline&prompt=consent&include_granted_scopes=true" +
        "&client_id=" + Uri.EscapeDataString(client.ClientId) + "&redirect_uri=" + Uri.EscapeDataString(redirect.AbsoluteUri) +
        "&scope=" + Uri.EscapeDataString(Scope) + "&code_challenge=" + challenge + "&code_challenge_method=S256&state=" + state);

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Returns the refresh token and the account address (from the id token returned by Google over TLS).</summary>
    public async Task<(string RefreshToken, string? Email)> ExchangeAsync(GoogleOAuthClient client, string code, string verifier, Uri redirect, CancellationToken token)
    {
        using var json = await TokenAsync(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret,
            ["redirect_uri"] = redirect.AbsoluteUri, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier
        }, token);
        var refresh = String(json.RootElement, "refresh_token") ?? throw new GoogleCalendarException(Loc.T("Google n’a pas accordé d’accès durable. Réessayez la connexion."));
        Remember(client, json.RootElement);
        return (refresh, Email(String(json.RootElement, "id_token")));
    }

    public async Task<GoogleEvent> InsertAsync(GoogleCalendarSecrets secrets, CalendarEntry entry, CancellationToken token)
    {
        var id = CalendarExport.Key(entry);
        using var response = await SendAsync(secrets, HttpMethod.Post, Events, Body(entry, id, null), token);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetAsync(secrets, id, token) ?? throw new GoogleCalendarException(Loc.T("Google Agenda a refusé l’événement."));
            if (!existing.IsCancelled) return existing;
            // Deleted by the user earlier and requested again explicitly: restore the same event.
            using var restored = await SendAsync(secrets, HttpMethod.Put, Events + "/" + id, Body(entry, null, "confirmed"), token);
            return await ReadEventAsync(restored, token);
        }
        return await ReadEventAsync(response, token);
    }

    public async Task<GoogleEvent?> GetAsync(GoogleCalendarSecrets secrets, string id, CancellationToken token)
    {
        using var response = await SendAsync(secrets, HttpMethod.Get, Events + "/" + id, null, token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        return await ReadEventAsync(response, token);
    }

    /// <summary>Best effort: the local token is deleted whatever Google answers.</summary>
    public async Task RevokeAsync(string refreshToken, CancellationToken token)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken });
            using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", content, token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        _accessToken = null;
    }

    private static string Body(CalendarEntry entry, string? id, string? status)
    {
        var body = new Dictionary<string, object?>
        {
            ["summary"] = entry.Title, ["description"] = entry.Description,
            ["start"] = new Dictionary<string, string> { ["dateTime"] = entry.StartsAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
            ["end"] = new Dictionary<string, string> { ["dateTime"] = entry.StartsAt.AddMinutes(5).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") },
            ["transparency"] = "transparent",
            ["reminders"] = new Dictionary<string, object> { ["useDefault"] = false, ["overrides"] = ReminderMinutes.Select(m => new Dictionary<string, object> { ["method"] = "popup", ["minutes"] = m }).ToArray() }
        };
        if (id is not null) body["id"] = id;
        if (status is not null) body["status"] = status;
        return JsonSerializer.Serialize(body);
    }

    private async Task<HttpResponseMessage> SendAsync(GoogleCalendarSecrets secrets, HttpMethod method, string url, string? json, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var access = await AccessTokenAsync(secrets, token);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, token); }
            catch (HttpRequestException) { throw new GoogleCalendarException(Loc.T("Google Agenda est injoignable. Vérifiez la connexion Internet.")); }
            catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new GoogleCalendarException(Loc.T("Google Agenda ne répond pas. Réessayez plus tard.")); }
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose(); _accessToken = null;
        }
    }

    private async Task<string> AccessTokenAsync(GoogleCalendarSecrets secrets, CancellationToken token)
    {
        if (!secrets.IsConnected) throw new GoogleCalendarException(Loc.T("Connectez d’abord Google Agenda dans les réglages."), true);
        if (_accessToken is not null && _tokenFor == secrets.RefreshToken && DateTimeOffset.UtcNow < _accessExpires) return _accessToken;
        using var json = await TokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = secrets.Client!.ClientId, ["client_secret"] = secrets.Client.ClientSecret,
            ["refresh_token"] = secrets.RefreshToken!, ["grant_type"] = "refresh_token"
        }, token);
        Remember(secrets.Client, json.RootElement);
        _tokenFor = secrets.RefreshToken;
        return _accessToken!;
    }

    private void Remember(GoogleOAuthClient client, JsonElement root)
    {
        _accessToken = String(root, "access_token") ?? throw new GoogleCalendarException(Loc.T("Réponse de Google inattendue."));
        var seconds = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var value) ? Math.Clamp(value, 60, 7200) : 600;
        _accessExpires = DateTimeOffset.UtcNow.AddSeconds(seconds - 30);
        _tokenFor = String(root, "refresh_token");
    }

    private async Task<JsonDocument> TokenAsync(Dictionary<string, string> form, CancellationToken token)
    {
        using var content = new FormUrlEncodedContent(form);
        HttpResponseMessage response;
        try { response = await http.PostAsync("https://oauth2.googleapis.com/token", content, token); }
        catch (HttpRequestException) { throw new GoogleCalendarException(Loc.T("Google Agenda est injoignable. Vérifiez la connexion Internet.")); }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new GoogleCalendarException(Loc.T("Google Agenda ne répond pas. Réessayez plus tard.")); }
        using (response)
        {
            var json = await ReadJsonAsync(response, token);
            if (response.IsSuccessStatusCode) return json;
            var error = String(json.RootElement, "error"); json.Dispose();
            // Revoked access, or a refresh token expired (OAuth client left in "Testing").
            if (error is "invalid_grant" or "unauthorized_client" or "invalid_client")
                throw new GoogleCalendarException(Loc.T("L’accès à Google Agenda a expiré ou a été retiré. Reconnectez Google Agenda dans les réglages."), true);
            throw new GoogleCalendarException(Loc.T("Google a refusé la connexion. Réessayez plus tard."));
        }
    }

    private static async Task<GoogleEvent> ReadEventAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            // Google names the cause in its error body; each one has its own fix.
            var reasons = await ReasonsAsync(response, token);
            if (reasons.Overlaps(["accessNotConfigured", "SERVICE_DISABLED"]))
                throw new GoogleCalendarException(Loc.T("L’API Google Calendar n’est pas encore active dans votre projet Google Cloud. Une API activée il y a moins de quelques minutes peut encore refuser : nouvel essai automatique dans 2 minutes."),
                    problem: GoogleCalendarProblem.ApiDisabled);
            if (reasons.Overlaps(["insufficientPermissions", "ACCESS_TOKEN_SCOPE_INSUFFICIENT"]))
                throw new GoogleCalendarException(Loc.T("L’accès à l’agenda n’a pas été accordé. Reconnectez Google Agenda et cochez l’accès aux événements."), true, GoogleCalendarProblem.MissingScope);
            if (reasons.Overlaps(["rateLimitExceeded", "userRateLimitExceeded", "RATE_LIMIT_EXCEEDED"]))
                throw new GoogleCalendarException(Loc.T("Google Agenda est momentanément indisponible. Réessayez plus tard."), problem: GoogleCalendarProblem.Unavailable);
            throw new GoogleCalendarException(Loc.T("Google Agenda refuse l’accès. Reconnectez Google Agenda dans les réglages."), true);
        }
        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
            throw new GoogleCalendarException(Loc.T("Google Agenda est momentanément indisponible. Réessayez plus tard."), problem: GoogleCalendarProblem.Unavailable);
        using var json = await ReadJsonAsync(response, token);
        if (!response.IsSuccessStatusCode) throw new GoogleCalendarException(Loc.T("Google Agenda a refusé l’événement."));
        var root = json.RootElement;
        var link = String(root, "htmlLink");
        return new(String(root, "id") ?? throw new GoogleCalendarException(Loc.T("Réponse de Google inattendue.")), String(root, "status") ?? "confirmed",
            link is not null && link.StartsWith("https://www.google.com/calendar/", StringComparison.Ordinal) || link?.StartsWith("https://calendar.google.com/", StringComparison.Ordinal) == true ? link : null);
    }

    /// <summary>The machine-readable reasons of a Google error: "errors[].reason" (v3) and "details[].reason" (newer gateway).</summary>
    private static async Task<HashSet<string>> ReasonsAsync(HttpResponseMessage response, CancellationToken token)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var json = await ReadJsonAsync(response, token);
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return reasons;
            foreach (var list in new[] { "errors", "details" })
                if (error.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var item in items.EnumerateArray().Take(10))
                        if (String(item, "reason") is { Length: <= 80 } reason) reasons.Add(reason);
        }
        catch (GoogleCalendarException) { }
        return reasons;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length > 262_144) throw new GoogleCalendarException(Loc.T("Réponse de Google inattendue."));
        try { return JsonDocument.Parse(bytes.Length == 0 ? "{}"u8.ToArray() : bytes, new() { MaxDepth = 32 }); }
        catch (JsonException) { throw new GoogleCalendarException(Loc.T("Réponse de Google inattendue.")); }
    }

    private static string? Email(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return String(json.RootElement, "email") is { Length: <= 320 } email ? email : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }
    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// One-shot loopback redirect for the OAuth consent (RFC 8252): bound to 127.0.0.1 on a random port,
/// answers only GET requests on "/", checks the state, then closes. Not a general HTTP server.
/// </summary>
public sealed class GoogleLoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    public Uri RedirectUri { get; }
    public GoogleLoopbackReceiver() { _listener.Start(1); RedirectUri = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/"); }

    public async Task<string> ReceiveCodeAsync(string state, CancellationToken token)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(token);
            await using var stream = client.GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string? line;
            try { line = await ReadRequestLineAsync(stream, timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { continue; }
            var result = Parse(line, state);
            await RespondAsync(stream, result.Status, result.Page, token);
            if (result.Error is not null) throw new GoogleCalendarException(result.Error);
            if (result.Code is not null) return result.Code;
        }
    }

    internal static (int Status, string Page, string? Code, string? Error) Parse(string? line, string state)
    {
        var parts = line?.Split(' ');
        if (parts is not { Length: 3 } || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) ||
            !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var uri) || uri.AbsolutePath != "/")
            return (404, "", null, null);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (query["state"] != state) return (400, Loc.T("Demande inattendue. Relancez la connexion depuis Codex Tracker."), null, null);
        if (query["error"] is { } error) return (200, Loc.T("Connexion annulée. Vous pouvez fermer cet onglet."), null,
            error == "access_denied" ? Loc.T("Connexion à Google Agenda annulée.") : Loc.T("Google a refusé la connexion. Réessayez plus tard."));
        return query["code"] is { Length: > 0 and <= 2048 } code
            ? (200, Loc.T("Google Agenda est connecté à Codex Tracker. Vous pouvez fermer cet onglet."), code, null)
            : (400, Loc.T("Demande inattendue. Relancez la connexion depuis Codex Tracker."), null, null);
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192]; var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) break;
            length += read;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
        }
        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken token)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Codex Tracker</title><body style=\"font-family:system-ui;margin:48px\"><p>" +
            WebUtility.HtmlEncode(message) + "</p></body>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = status switch { 200 => "OK", 400 => "Bad Request", _ => "Not Found" };
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        try { await stream.WriteAsync(header, token); await stream.WriteAsync(body, token); }
        catch (IOException) { }
    }

    public void Dispose() => _listener.Stop();
}
