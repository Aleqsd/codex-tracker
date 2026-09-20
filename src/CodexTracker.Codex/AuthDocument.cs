using System.Globalization;
using System.Text.Json;

namespace CodexTracker.Codex;

// JWT claims describe the locally observed session; authentication remains owned by the Codex app.
internal sealed record AuthDocument(string Email, string AccountId, string AccessToken, string? PlanType,
    DateTimeOffset? SubscriptionStartedAt = null, DateTimeOffset? SubscriptionEndsAt = null)
{
    public static AuthDocument Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            throw new TrackerException("Cette session ne contient pas de connexion ChatGPT gérée par Codex.");
        var access = Read(tokens, "access_token") ?? throw new TrackerException("La session Codex est incomplète. Ouvrez ce compte dans Codex pour l'actualiser.");
        var id = Read(tokens, "id_token");
        using var claims = ParseClaims(id ?? access);
        using var accessClaims = ParseClaims(access);
        var email = Read(claims.RootElement, "email") ?? Nested(accessClaims.RootElement, "https://api.openai.com/profile", "email");
        var accountId = Read(tokens, "account_id") ?? Nested(claims.RootElement, "https://api.openai.com/auth", "chatgpt_account_id")
            ?? Nested(accessClaims.RootElement, "https://api.openai.com/auth", "chatgpt_account_id");
        var plan = Nested(claims.RootElement, "https://api.openai.com/auth", "chatgpt_plan_type")
            ?? Nested(accessClaims.RootElement, "https://api.openai.com/auth", "chatgpt_plan_type");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(accountId))
            throw new TrackerException("L'identité de cette session est indisponible. Ouvrez ce compte dans Codex pour l'actualiser.");
        // These fields describe the active subscription period, never token issuance or token expiry.
        var startedAt = id is null ? null : ReadSubscriptionTimestamp(claims.RootElement, "chatgpt_subscription_active_start");
        var endsAt = id is null ? null : ReadSubscriptionTimestamp(claims.RootElement, "chatgpt_subscription_active_until");
        return new(email, accountId, access, plan, startedAt, endsAt);
    }

    internal static int? PlanMultiplier(string? plan) => plan?.Trim().ToLowerInvariant() switch
    {
        "prolite" => 5,
        "pro" => 20,
        _ => null
    };

    private static DateTimeOffset? ReadSubscriptionTimestamp(JsonElement claims, string name)
    {
        var value = Nested(claims, "https://api.openai.com/auth", name);
        // Require ISO 8601 with an explicit offset; ambiguous local dates and numeric epochs stay unknown.
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];
        return DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
    }

    private static JsonDocument ParseClaims(string token)
    {
        var pieces = token.Split('.');
        if (pieces.Length != 3) throw new TrackerException("Le format de la session Codex n'est pas pris en charge.");
        var value = pieces[1].Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        try
        {
            var document = JsonDocument.Parse(Convert.FromBase64String(value));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            { document.Dispose(); throw new TrackerException("Le format de la session Codex n'est pas pris en charge."); }
            return document;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new TrackerException("Le format de la session Codex n'est pas pris en charge."); }
    }

    internal static string? Read(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string? Nested(JsonElement value, string container, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(container, out var inner) ? Read(inner, name) : null;
}

public sealed class TrackerException(string message) : Exception(message);
