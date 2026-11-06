using System.Diagnostics;

namespace CodexTracker.Codex;

/// <summary>
/// Advances a bounded metadata-only enumeration instead of rereading the same first 1,024 session names.
/// Keeps one Code candidate or 32 agent candidates per directory; current attributes are checked on every read.
/// </summary>
internal static class ClaudeSessionMetadataScan
{
    private const int BatchSize = 1024, AgentBatchSize = 256, MaximumAgentCandidates = 32, MaximumCursors = 64;
    // A partial sweep must survive the adaptive 10-minute cadence and the 30-minute retry backoff.
    private static readonly TimeSpan MaximumIdle = TimeSpan.FromHours(1);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Cursor> Cursors = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Cursor : IDisposable
    {
        internal IEnumerator<FileInfo>? Files;
        internal IEnumerator<DirectoryInfo>? Directories;
        internal Dictionary<string, DateTimeOffset> AgentCandidates = new(StringComparer.OrdinalIgnoreCase);
        internal string? LatestPath;
        internal long LastUsed;
        public void Dispose() { Files?.Dispose(); Files = null; Directories?.Dispose(); Directories = null; }
    }

    internal static DateTimeOffset? Read(DirectoryInfo directory, DateTimeOffset now, CancellationToken token)
    {
        lock (Sync)
        {
            token.ThrowIfCancellationRequested();
            var cursor = GetCursor(directory.FullName);
            try
            {
                directory.Refresh();
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                { Remove(directory.FullName); return null; }
                var latest = cursor.LatestPath is { } known ? ReadDate(new FileInfo(known), now) : null;
                if (latest is null) cursor.LatestPath = null;
                cursor.Files ??= directory.EnumerateFiles("local_*.json").GetEnumerator();
                for (var index = 0; index < BatchSize; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!cursor.Files.MoveNext()) { cursor.Dispose(); break; }
                    var file = cursor.Files.Current;
                    var at = ReadDate(file, now);
                    if (at is null || at <= latest) continue;
                    latest = at;
                    cursor.LatestPath = file.FullName;
                }
                return latest;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { Remove(directory.FullName); return null; }
            catch (OperationCanceledException) { cursor.Dispose(); throw; }
        }
    }

    /// <summary>Progresses through agent directories too, retaining only the 32 newest valid metadata candidates.</summary>
    internal static IReadOnlyList<DirectoryInfo> ReadAgents(DirectoryInfo directory, CancellationToken token)
    {
        lock (Sync)
        {
            token.ThrowIfCancellationRequested();
            var cursor = GetCursor(directory.FullName);
            try
            {
                directory.Refresh();
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                { Remove(directory.FullName); return []; }
                foreach (var path in cursor.AgentCandidates.Keys.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (ReadAgentDate(new DirectoryInfo(path)) is { } at) cursor.AgentCandidates[path] = at;
                    else cursor.AgentCandidates.Remove(path);
                }
                cursor.Directories ??= directory.EnumerateDirectories("local_*").GetEnumerator();
                for (var index = 0; index < AgentBatchSize; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!cursor.Directories.MoveNext()) { cursor.Dispose(); break; }
                    var session = cursor.Directories.Current;
                    if (ReadAgentDate(session) is not { } at) continue;
                    cursor.AgentCandidates[session.FullName] = at;
                    if (cursor.AgentCandidates.Count > MaximumAgentCandidates)
                        cursor.AgentCandidates.Remove(cursor.AgentCandidates.MinBy(pair => pair.Value).Key);
                }
                return cursor.AgentCandidates.OrderByDescending(pair => pair.Value).Select(pair => new DirectoryInfo(pair.Key)).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
            { Remove(directory.FullName); return []; }
            catch (OperationCanceledException) { cursor.Dispose(); throw; }
        }
    }

    private static DateTimeOffset? ReadAgentDate(DirectoryInfo directory)
    {
        try
        {
            // Directory enumeration can buffer attributes across refresh calls.
            directory.Refresh();
            if (!directory.Name.StartsWith("local_", StringComparison.Ordinal) || !Guid.TryParseExact(directory.Name[6..], "D", out _) ||
                !directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return new DateTimeOffset(directory.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return null; }
    }

    private static Cursor GetCursor(string path)
    {
        var stamp = Stopwatch.GetTimestamp();
        foreach (var expired in Cursors.Where(pair => Stopwatch.GetElapsedTime(pair.Value.LastUsed, stamp) > MaximumIdle).Select(pair => pair.Key).ToArray())
            Remove(expired);
        if (!Cursors.TryGetValue(path, out var cursor))
        {
            if (Cursors.Count >= MaximumCursors) Remove(Cursors.MinBy(pair => pair.Value.LastUsed).Key);
            Cursors[path] = cursor = new();
        }
        cursor.LastUsed = stamp;
        return cursor;
    }

    private static DateTimeOffset? ReadDate(FileInfo file, DateTimeOffset now)
    {
        try
        {
            // A retained enumerator may have prefetched this entry before its latest native write.
            file.Refresh();
            var name = Path.GetFileNameWithoutExtension(file.Name);
            if (name.Length < 7 || !Guid.TryParseExact(name[6..], "D", out _) || !file.Exists ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var at = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
            return at > now.AddMinutes(1) || at < now.AddDays(-30) ? null : at;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException) { return null; }
    }

    internal static void Release(IEnumerable<string> roots)
    {
        var prefixes = roots.Select(root => root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToArray();
        lock (Sync)
            foreach (var path in Cursors.Keys.Where(path => prefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray()) Remove(path);
    }

    private static void Remove(string path)
    {
        if (Cursors.Remove(path, out var cursor)) cursor.Dispose();
    }
}
