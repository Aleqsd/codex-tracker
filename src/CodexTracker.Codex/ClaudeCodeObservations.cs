using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

// The short-lived hook process writes only this inbox. The WPF process remains the sole owner of profiles/preferences.
public sealed class ClaudeCodeObservations(string dataDirectory, ClaudeCodeLocation location)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root = Path.Combine(Path.GetFullPath(dataDirectory), "claude-observations");
    public string SignalPath => Path.Combine(_root, "latest.json");
    private sealed record Session(string AccountId, string Email, DateTimeOffset StartedAt);
    private sealed record Observation(string AccountId, AccountSnapshot Snapshot);
    private static string Key(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private string AccountPath(string id) => Path.Combine(_root, "account-" + Key(id) + ".json");
    private string SessionPath(Guid id) => Path.Combine(_root, "session-" + id.ToString("N") + ".json");

    public static string Configuration(string executable) => JsonSerializer.Serialize(new
    {
        hooks = new { SessionStart = new[] { new { matcher = "startup|resume", hooks = new[] { new { type = "command", command = Command(executable, "--claude-session-start"), timeout = 5 } } } } },
        statusLine = new { type = "command", command = Command(executable, "--claude-statusline") }
    }, new JsonSerializerOptions { WriteIndented = true });

    // Invoke through PowerShell explicitly: the same command also works when Claude uses Git Bash.
    private static string Command(string executable, string mode)
    {
        var path = Path.GetFullPath(executable).Replace('\\', '/').Replace("'", "''");
        var script = $"$ProgressPreference = 'SilentlyContinue'; $OutputEncoding = [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $input | & '{path}' {mode}";
        return "powershell -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    public sealed record TerminalStatus(bool Configured, DateTimeOffset? LastReading);

    /// <summary>
    /// Whether Claude Code's settings call this collector, and when it last delivered quotas.
    /// Only the settings file and the inbox file dates are read: no session, quota or token content.
    /// </summary>
    public TerminalStatus ReadStatus(string executable)
    {
        var configured = false;
        try
        {
            var settings = location.CredentialsPath is { } credentials ? Path.Combine(Path.GetDirectoryName(credentials)!, "settings.json") : null;
            if (settings is not null && File.Exists(settings))
            {
                var text = File.ReadAllText(settings);
                configured = text.Contains("--claude-statusline", StringComparison.Ordinal) || text.Contains(Command(executable, "--claude-statusline"), StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        DateTimeOffset? last = null;
        try
        {
            if (Directory.Exists(_root))
                last = Directory.EnumerateFiles(_root, "account-*.json").Select(path => (DateTimeOffset?)new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)).Max();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(configured, last);
    }

    public async Task<string?> CaptureAsync(string input, bool sessionStart, DateTimeOffset now, CancellationToken token = default)
    {
        if (input.Length > 1024 * 1024) throw new JsonException();
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !Guid.TryParse(ClaudeCodeIdentity.Text(root, "session_id"), out var sessionId) || sessionId == Guid.Empty)
            return null;
        var identity = await ClaudeCodeIdentity.ReadAsync(location, token);
        if (identity.ExpiresAt <= now) return null;
        ProfileStore.SecureDirectory(_root);
        // Serialise callbacks from simultaneous native sessions and never replace a newer observation.
        using var mutex = new Mutex(false, "Local\\CodexTracker.ClaudeObservations." + Key(_root.ToUpperInvariant()));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(1)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return null;
            if (sessionStart)
            {
                if (ClaudeCodeIdentity.Text(root, "source") is not ("startup" or "resume")) return null;
                var existing = Read<Session>(SessionPath(sessionId));
                if (existing is not null && (existing.AccountId != identity.AccountId || !SameEmail(existing.Email, identity.Email))) return null;
                Write(SessionPath(sessionId), new Session(identity.AccountId, identity.Email, now));
                foreach (var path in Directory.EnumerateFiles(_root, "session-*.json"))
                    if (File.GetLastWriteTimeUtc(path) < now.UtcDateTime.AddDays(-30)) File.Delete(path);
                return null;
            }
            // Bind quotas to the account observed when the native session started. A delayed old
            // session cannot publish its quotas under an account that was opened afterwards.
            var session = Read<Session>(SessionPath(sessionId));
            if (session is null || session.AccountId != identity.AccountId || !SameEmail(session.Email, identity.Email) ||
                session.StartedAt > now || now - session.StartedAt > TimeSpan.FromDays(30)) return null;
            var snapshot = ClaudeCodeQuotaParser.Parse(root, identity.Email, identity.PlanType, now, identity.PlanMultiplier);
            if (snapshot is null) return null;
            var pathForAccount = AccountPath(identity.AccountId);
            if (Read<Observation>(pathForAccount)?.Snapshot.FetchedAt > now) return null;
            Write(pathForAccount, new Observation(identity.AccountId, snapshot));
            Write(SignalPath, new { observedAt = now, account = Key(identity.AccountId) });
            return Loc.F("Claude Code · 5 h : {0} · semaine : {1}", Percent(snapshot.Short), Percent(snapshot.Weekly));
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    internal AccountSnapshot Read(ClaudeCodeIdentity identity)
    {
        if (identity.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new TrackerException(Loc.T("La session Claude Code a expiré. Ouvrez Claude Code pour renouveler votre connexion ; le dernier relevé est conservé."));
        var observation = Read<Observation>(AccountPath(identity.AccountId));
        var snapshot = observation?.Snapshot;
        if (observation?.AccountId != identity.AccountId || snapshot is null || !SameEmail(snapshot.Email, identity.Email) ||
            snapshot.FetchedAt > DateTimeOffset.UtcNow.AddMinutes(1) || snapshot.Buckets is not { Count: 1 } ||
            snapshot.Buckets[0] is not { Id: "claude", Windows: not null } bucket || bucket.Windows.Count is < 1 or > 2 ||
            bucket.Windows.Any(w => w is null || !double.IsFinite(w.UsedPercent) || w.UsedPercent is < 0 or > 100 || w.WindowDurationMins is not (300 or 10080)))
            throw new TrackerException(Loc.T("Quotas Claude Code en attente. Activez les relevés dans Réglages → Général, puis utilisez Claude Code."));
        return snapshot;
    }

    public async Task RunAsync(TextReader input, TextWriter output, bool sessionStart, CancellationToken token = default)
    {
        // Claude's task must continue if collection fails; no raw payload, paths or exception text is printed.
        try
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                if (text.Length + read > 1024 * 1024) return;
                text.Append(buffer, 0, read);
            }
            var line = await CaptureAsync(text.ToString(), sessionStart, DateTimeOffset.UtcNow, token);
            if (line is not null) { await output.WriteLineAsync(line.AsMemory(), token); await output.FlushAsync(token); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or TrackerException or OperationCanceledException) { }
    }

    private static string Percent(QuotaWindow? window) => window is null ? "—" : Loc.F("{0:0}% restant", Math.Floor(window.RemainingPercent));
    private static bool SameEmail(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 1024 * 1024) throw new JsonException();
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json);
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
