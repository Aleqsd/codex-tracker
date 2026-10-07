using System.Text.Json;

namespace CodexTracker.Core;

/// <summary>Reads Claude Code's documented status-line input, not token counts or internal HTTP endpoints.</summary>
public static class ClaudeCodeQuotaParser
{
    public static AccountSnapshot? Parse(JsonElement input, string email, string? plan, DateTimeOffset observedAt, int? multiplier = null)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return null;
        var windows = new List<QuotaWindow>();
        Add("five_hour", 300);
        Add("seven_day", 10080);
        return windows.Count == 0 ? null : new(email, plan, [new("claude", "Claude Code", windows)], null, null, observedAt, multiplier);

        void Add(string name, int minutes)
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind == JsonValueKind.Null) return;
            if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("used_percentage", out var used) ||
                used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent is < 0 or > 100)
                throw new JsonException("Quota Claude Code invalide.");
            DateTimeOffset? resets = null;
            if (window.TryGetProperty("resets_at", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds)) throw new JsonException("Date Claude Code invalide.");
                try { resets = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { throw new JsonException("Date Claude Code invalide."); }
            }
            windows.Add(new(percent, minutes, resets));
        }
    }
}
