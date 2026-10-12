using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

// Only the cached usage response of the selected organization is decoded. No request,
// cookie store, conversation, native configuration change, or account switch is involved.
internal static class ClaudeDesktopCacheUsageReader
{
    internal const int MaximumBodyBytes = 128 * 1024;

    internal static AccountSnapshot? Read(IReadOnlyList<string> historyPaths, ClaudeCodeIdentity identity,
        DateTimeOffset now, CancellationToken token)
    {
        var separator = identity.AccountId.IndexOf('/');
        if (separator < 0 || !Guid.TryParse(identity.AccountId[(separator + 1)..], out var organization)) return null;
        ChromiumUsageCache.Response? latest = null;
        foreach (var directory in historyPaths.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "Cache", "Cache_Data"))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = ChromiumUsageCache.Read(directory, organization, now, token);
                if (response is not null && (latest is null || response.ObservedAt > latest.ObservedAt)) latest = response;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException) { }
        }
        if (latest is null) return null;
        try
        {
            using var document = JsonDocument.Parse(Decode(latest.Body, latest.Encoding), new() { MaxDepth = 16 });
            return Parse(document.RootElement, identity, latest.ObservedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or InvalidOperationException or ZstdSharp.ZstdException) { return null; }
    }

    internal static byte[] Decode(byte[] body, string encoding)
    {
        if (body.Length > MaximumBodyBytes) throw new InvalidDataException();
        if (encoding is "" or "identity") return body;
        if (encoding == "zstd")
        {
            using var decompressor = new ZstdSharp.Decompressor();
            decompressor.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 18);
            return decompressor.Unwrap(body, MaximumBodyBytes).ToArray();
        }
        using var source = new MemoryStream(body, writable: false);
        using Stream decoded = encoding switch
        {
            "br" => new BrotliStream(source, CompressionMode.Decompress),
            "gzip" => new GZipStream(source, CompressionMode.Decompress),
            "deflate" => new ZLibStream(source, CompressionMode.Decompress),
            _ => throw new InvalidDataException()
        };
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = decoded.Read(buffer)) > 0)
        {
            if (output.Length + read > MaximumBodyBytes) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static AccountSnapshot? Parse(JsonElement root, ClaudeCodeIdentity identity, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var windows = new List<QuotaWindow>();
        foreach (var (field, minutes) in new[] { ("five_hour", 300), ("seven_day", 10080) })
        {
            if (!root.TryGetProperty(field, out var window) || window.ValueKind == JsonValueKind.Null) continue;
            if (window.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (!window.TryGetProperty("utilization", out var used) || used.ValueKind == JsonValueKind.Null) continue;
            if (used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) ||
                !double.IsFinite(percent) || percent is < 0 or > 100) throw new JsonException();
            DateTimeOffset? reset = null;
            if (window.TryGetProperty("resets_at", out var date) && date.ValueKind != JsonValueKind.Null)
            {
                if (date.ValueKind != JsonValueKind.String) throw new JsonException();
                var text = date.GetString();
                if (text is not { Length: >= 20 } || !(text.EndsWith('Z') || text[^6] is '+' or '-' && text[^3] == ':') ||
                    !DateTimeOffset.TryParseExact(text,
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ||
                    at < observedAt.AddDays(-30) || at > observedAt.AddMinutes(minutes + 5)) throw new JsonException();
                reset = at;
            }
            windows.Add(new(percent, minutes, reset));
        }
        return windows.Count == 0 ? null : new(identity.Email, identity.PlanType, [new("claude", "Claude Code", windows)],
            null, null, observedAt, identity.PlanMultiplier);
    }
}
