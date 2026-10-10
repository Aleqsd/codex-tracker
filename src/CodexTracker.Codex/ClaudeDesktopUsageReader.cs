using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

// Passive adapter for the Desktop application's versioned native usage-history cache.
// This cache contains utilization and observation dates, but no reset deadlines or credentials.
internal static class ClaudeDesktopUsageReader
{
    internal static IReadOnlyList<string> ResolvePaths(string roaming, string local)
    {
        var paths = new List<string> { Path.Combine(roaming, "Claude", "plan-usage-history.json") };
        var packages = Path.Combine(local, "Packages");
        try
        {
            if (Directory.Exists(packages))
                paths.AddRange(Directory.EnumerateDirectories(packages, "Claude_*")
                    .Select(p => Path.Combine(p, "LocalCache", "Roaming", "Claude", "plan-usage-history.json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static async Task<AccountSnapshot?> ReadAsync(IReadOnlyList<string> paths, ClaudeCodeIdentity identity,
        CancellationToken token)
    {
        var separator = identity.AccountId.IndexOf('/');
        if (separator < 0 || !Guid.TryParse(identity.AccountId[(separator + 1)..], out var organization)) return null;
        AccountSnapshot? latest = null;
        foreach (var path in paths)
        {
            try
            {
                using var document = await ClaudeCodeIdentity.ReadDocumentAsync(path, token);
                var snapshot = Parse(document.RootElement, identity, organization, DateTimeOffset.UtcNow);
                if (snapshot is not null && (latest is null || snapshot.FetchedAt > latest.FetchedAt)) latest = snapshot;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return latest;
    }

    /// <summary>Weekly usage readings of the selected organization, oldest first, to locate an observed weekly reset.</summary>
    internal static async Task<IReadOnlyList<(DateTimeOffset At, double Used)>> ReadWeeklyAsync(IReadOnlyList<string> paths,
        ClaudeCodeIdentity identity, CancellationToken token)
    {
        var separator = identity.AccountId.IndexOf('/');
        if (separator < 0 || !Guid.TryParse(identity.AccountId[(separator + 1)..], out var organization)) return [];
        var readings = new List<(DateTimeOffset, double)>();
        foreach (var path in paths)
        {
            try
            {
                using var document = await ClaudeCodeIdentity.ReadDocumentAsync(path, token);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 2 ||
                    !root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) continue;
                foreach (var sample in samples.EnumerateArray())
                {
                    if (sample.ValueKind != JsonValueKind.Object || !Guid.TryParse(ClaudeCodeIdentity.Text(sample, "org"), out var org) || org != organization ||
                        !sample.TryGetProperty("t", out var t) || !t.TryGetInt64(out var milliseconds) ||
                        !sample.TryGetProperty("u", out var usage) || usage.ValueKind != JsonValueKind.Object ||
                        !usage.TryGetProperty("sd", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var value)) continue;
                    try { readings.Add((DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), value)); }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        return readings;
    }

    internal static AccountSnapshot? Parse(JsonElement root, ClaudeCodeIdentity identity, Guid organization, DateTimeOffset now)
    {
        // Version 1 had no organization identifier. It cannot safely be assigned to a personal/work account.
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 2 ||
            !root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array) return null;
        JsonElement latest = default;
        DateTimeOffset? observedAt = null;
        foreach (var sample in samples.EnumerateArray())
        {
            if (sample.ValueKind != JsonValueKind.Object || !Guid.TryParse(ClaudeCodeIdentity.Text(sample, "org"), out var org) || org != organization ||
                !sample.TryGetProperty("t", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number || !timestamp.TryGetInt64(out var milliseconds)) continue;
            DateTimeOffset time;
            try { time = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (time > now || time < now.AddDays(-30) || time <= observedAt) continue;
            observedAt = time; latest = sample;
        }
        if (observedAt is null || !latest.TryGetProperty("u", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var windows = new List<QuotaWindow>();
        foreach (var (field, duration) in new[] { ("fh", 300), ("sd", 10080) })
        {
            if (!usage.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var used) || !double.IsFinite(used) || used is < 0 or > 100) return null;
            windows.Add(new(used, duration, null));
        }
        return windows.Count == 0 ? null : new(identity.Email, identity.PlanType, [new("claude", "Claude Code", windows)],
            null, null, observedAt.Value, PlanMultiplier: identity.PlanMultiplier);
    }
}
