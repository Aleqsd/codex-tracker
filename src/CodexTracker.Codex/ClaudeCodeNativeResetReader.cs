using System.Globalization;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

/// <summary>Exact reset metadata from native Desktop Code quota errors, never from conversation text.</summary>
// The Desktop namespace binds a CLI session to BOTH account and organization. A bare CLI
// transcript or its representative quotaLimits state cannot establish that association.
internal static class ClaudeCodeNativeResetReader
{
    internal const int MaximumTailBytes = 512 * 1024, MaximumRecordBytes = 64 * 1024;
    private const int MaximumSessions = 16, MaximumProjectDirectories = 512, MaximumMetadataBytes = 1024 * 1024;
    internal sealed record Reading(DateTimeOffset ResetsAt, DateTimeOffset ObservedAt);

    internal static Reading? Read(ClaudeCodeLocation location, IReadOnlyList<string> desktopPaths,
        ClaudeCodeIdentity identity, DateTimeOffset now, CancellationToken token)
    {
        var parts = identity.AccountId.Split('/');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var account) || account == Guid.Empty ||
            !Guid.TryParse(parts[1], out var organization) || organization == Guid.Empty || location.CredentialsPath is null) return null;
        var projects = Path.Combine(Path.GetDirectoryName(location.CredentialsPath)!, "projects");
        var sessions = new HashSet<Guid>();
        foreach (var root in desktopPaths.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "claude-code-sessions",
            account.ToString("D"), organization.ToString("D"))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!RegularDirectories(root, 3)) continue;
                foreach (var path in Directory.EnumerateFiles(root, "local_*.json").Take(256)
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(MaximumSessions))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        if (!Guid.TryParseExact(name[6..], "D", out _) || !RegularFile(path)) continue;
                        using var file = Open(path);
                        var length = file.Length;
                        if (length is < 2 or > MaximumMetadataBytes) continue;
                        var bytes = new byte[(int)length]; file.ReadExactly(bytes);
                        if (Session(bytes, name) is { } session) sessions.Add(session);
                    }
                    catch (Exception ex) when (Recoverable(ex)) { }
                }
            }
            catch (Exception ex) when (Recoverable(ex)) { }
        }
        if (sessions.Count == 0) return null;
        Reading? latest = null;
        try
        {
            if (!RegularDirectories(projects, 2)) return null;
            foreach (var directory in Directory.EnumerateDirectories(projects).Take(MaximumProjectDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (!RegularDirectories(directory, 1)) continue;
                foreach (var session in sessions)
                {
                    var path = Path.Combine(directory, session.ToString("D") + ".jsonl");
                    try
                    {
                        if (!File.Exists(path) || !RegularFile(path)) continue;
                        using var file = Open(path);
                        var length = file.Length;
                        var start = Math.Max(0, length - MaximumTailBytes);
                        file.Position = start;
                        var tail = new byte[(int)(length - start)]; file.ReadExactly(tail);
                        var reading = Tail(tail, start != 0, session, now, token, includeEnded: true);
                        if (reading is not null && (latest is null || reading.ObservedAt > latest.ObservedAt)) latest = reading;
                    }
                    catch (Exception ex) when (Recoverable(ex)) { }
                }
            }
        }
        catch (Exception ex) when (Recoverable(ex)) { }
        return latest?.ResetsAt > now ? latest : null;
    }

    internal static Guid? Session(ReadOnlySpan<byte> bytes, string expectedName)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        string? name = null, id = null;
        var seen = new HashSet<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return null;
            var property = reader.GetString()!;
            if (!reader.Read()) return null;
            if (property is "sessionId" or "cliSessionId")
            {
                if (!seen.Add(property) || reader.TokenType != JsonTokenType.String) return null;
                if (property == "sessionId") name = reader.GetString(); else id = reader.GetString();
            }
            else reader.Skip();
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || name != expectedName) return null;
        return Guid.TryParseExact(id, "D", out var session) && session != Guid.Empty ? session : null;
    }

    internal static Reading? Tail(ReadOnlySpan<byte> bytes, bool partialStart, Guid session, DateTimeOffset now, CancellationToken token,
        bool includeEnded = false)
    {
        Reading? latest = null;
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var end = bytes[cursor..].IndexOf((byte)'\n');
            if (end < 0) break; // An unfinished append is not an observation.
            var line = bytes.Slice(cursor, end); cursor += end + 1;
            if (partialStart) { partialStart = false; continue; }
            if (line.Length > MaximumRecordBytes || line.IndexOf("\"apiErrorParams\""u8) < 0) continue;
            try
            {
                var reading = Parse(line, session, now, includeEnded: true);
                if (reading is not null && (latest is null || reading.ObservedAt > latest.ObservedAt)) latest = reading;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentOutOfRangeException) { }
        }
        return includeEnded || latest?.ResetsAt > now ? latest : null;
    }

    internal static Reading? Parse(ReadOnlySpan<byte> bytes, Guid expectedSession, DateTimeOffset now, bool includeEnded = false)
    {
        // Skip every message/content block without materializing it. Only these top-level
        // native error annotations can provide a reset. User/assistant text is never parsed.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 64 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        string? type = null, error = null, version = null, session = null, timestamp = null;
        bool apiError = false;
        int? status = null;
        Reading? reset = null;
        var seen = new HashSet<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return null;
            var property = reader.GetString()!;
            if (!reader.Read()) return null;
            if (property is "type" or "error" or "version" or "sessionId" or "timestamp" or
                "isApiErrorMessage" or "apiErrorStatus" or "apiErrorParams")
            {
                if (!seen.Add(property)) return null;
                switch (property)
                {
                    case "isApiErrorMessage": apiError = reader.TokenType == JsonTokenType.True; break;
                    case "apiErrorStatus": status = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var code) ? code : null; break;
                    case "apiErrorParams": reset = ResponseReset(ref reader, now); break;
                    default:
                        if (reader.TokenType != JsonTokenType.String) return null;
                        var value = reader.GetString();
                        switch (property)
                        {
                            case "type": type = value; break;
                            case "error": error = value; break;
                            case "version": version = value; break;
                            case "sessionId": session = value; break;
                            case "timestamp": timestamp = value; break;
                        }
                        break;
                }
            }
            else reader.Skip();
        }
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || type != "assistant" || error != "rate_limit" ||
            !apiError || status != 429 || version != "2.1.293" || !Guid.TryParseExact(session, "D", out var id) || id != expectedSession || reset is null ||
            timestamp is not { Length: >= 20 } || !(timestamp.EndsWith('Z') || timestamp[^6] is '+' or '-' && timestamp[^3] == ':') ||
            !DateTimeOffset.TryParseExact(timestamp, "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.None, out var observedAt) ||
            observedAt > now || observedAt < now.AddMinutes(-305) || reset.ResetsAt <= observedAt ||
            reset.ResetsAt > observedAt.AddMinutes(305) || !includeEnded && reset.ResetsAt <= now) return null;
        return reset with { ObservedAt = observedAt };
    }

    private static Reading? ResponseReset(ref Utf8JsonReader reader, DateTimeOffset now)
    {
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return null; }
        Reading? result = null;
        var seen = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var rateInfo = reader.ValueTextEquals("rate_limit_info");
            if (!reader.Read()) throw new JsonException();
            if (!rateInfo) { reader.Skip(); continue; }
            if (seen) throw new JsonException();
            seen = true;
            if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); continue; }
            string? type = null, status = null;
            long? seconds = null;
            var fields = new HashSet<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                var property = reader.GetString()!;
                if (!reader.Read()) throw new JsonException();
                if (property is "status" or "rateLimitType" or "resetsAt")
                {
                    if (!fields.Add(property)) throw new JsonException();
                    if (property == "resetsAt") seconds = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var value) ? value : null;
                    else if (reader.TokenType == JsonTokenType.String)
                    {
                        if (property == "status") status = reader.GetString(); else type = reader.GetString();
                    }
                    else reader.Skip();
                }
                else reader.Skip();
            }
            if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException();
            if (type != "five_hour" || status != "rejected" || seconds is null) continue;
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
            if (reset > now.AddMinutes(-305) && reset <= now.AddMinutes(305)) result = new(reset, default);
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException();
        return result;
    }

    internal static AccountSnapshot Apply(AccountSnapshot snapshot, Reading? reading, DateTimeOffset now)
    {
        if (reading is null || reading.ResetsAt <= now || snapshot.Short is not { } window || window.ResetsAt > now ||
            reading.ObservedAt < snapshot.FetchedAt && window.UsedPercent < 100) return snapshot;
        // Do not re-date the percentages or infer their restoration from this exact deadline.
        return snapshot with { Buckets = snapshot.Buckets.Select(b => b with { Windows = b.Windows.Select(w =>
            w.WindowDurationMins == 300 ? w with { ResetsAt = reading.ResetsAt } : w).ToArray() }).ToArray() };
    }

    private static bool RegularFile(string path) => (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
    private static bool RegularDirectories(string path, int count)
    {
        for (var directory = new DirectoryInfo(path); count > 0; count--, directory = directory.Parent!)
            if (directory is null || !directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }
    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    private static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or
        InvalidOperationException or ArgumentException or NotSupportedException;
}
