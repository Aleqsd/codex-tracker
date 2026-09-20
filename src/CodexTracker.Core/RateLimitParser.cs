using System.Text.Json;

namespace CodexTracker.Core;

/// <summary>Converts the Codex account/rateLimits/read result without estimating unavailable data.</summary>
public static class RateLimitParser
{
    public static AccountSnapshot Parse(JsonElement result, string email, string? planType,
        DateTimeOffset fetchedAt)
    {
        if (result.ValueKind != JsonValueKind.Object)
            throw new JsonException("The rate limit response must be a JSON object.");

        var buckets = new List<QuotaBucket>();
        string? bucketPlan = null;
        var map = Property(result, "rateLimitsByLimitId");
        if (map.ValueKind == JsonValueKind.Object)
        {
            // The map is authoritative, including an empty map. Never append the legacy
            // bucket or aggregate unrelated model buckets into the core Codex quota.
            foreach (var entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                buckets.Add(ParseBucket(entry.Name, entry.Value));
                var candidatePlan = String(entry.Value, "planType");
                if (bucketPlan is null || entry.Name == "codex")
                    bucketPlan = candidatePlan ?? bucketPlan;
            }
        }
        else
        {
            var legacy = Property(result, "rateLimits");
            if (legacy.ValueKind == JsonValueKind.Object)
            {
                buckets.Add(ParseBucket(String(legacy, "limitId") ?? "codex", legacy));
                bucketPlan = String(legacy, "planType");
            }
        }

        var resetSummary = Property(result, "rateLimitResetCredits");
        var available = NonNegativeInt(resetSummary, "availableCount");
        IReadOnlyList<ResetCredit>? resetCredits = null;
        var details = Property(resetSummary, "credits");
        if (details.ValueKind == JsonValueKind.Array)
        {
            resetCredits = details.EnumerateArray()
                .Where(row => row.ValueKind == JsonValueKind.Object && String(row, "id") is not null)
                .Select(row => new ResetCredit(String(row, "id")!, String(row, "title"),
                    Timestamp(row, "grantedAt"), Timestamp(row, "expiresAt")))
                .ToArray();
        }

        // A detail list may be capped. Its length must never stand in for availableCount.
        return new AccountSnapshot(email, NonBlank(planType) ?? bucketPlan, buckets,
            available, resetCredits, fetchedAt);
    }

    private static QuotaBucket ParseBucket(string id, JsonElement value)
    {
        var windows = new List<QuotaWindow>(2);
        foreach (var key in new[] { "primary", "secondary" })
        {
            var window = Property(value, key);
            var percent = Property(window, "usedPercent");
            if (percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used)
                || !double.IsFinite(used)) continue;

            var duration = NonNegativeInt(window, "windowDurationMins");
            windows.Add(new QuotaWindow(Math.Clamp(used, 0, 100),
                duration is > 0 ? duration : null, Timestamp(window, "resetsAt")));
        }
        return new QuotaBucket(id, String(value, "limitName"), windows);
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property : default;

    private static string? String(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind == JsonValueKind.String ? NonBlank(property.GetString()) : null;
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? NonNegativeInt(JsonElement value, string name)
    {
        var property = Property(value, name);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number)
            && number >= 0 ? number : null;
    }

    private static DateTimeOffset? Timestamp(JsonElement value, string name)
    {
        var property = Property(value, name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var seconds))
            return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
