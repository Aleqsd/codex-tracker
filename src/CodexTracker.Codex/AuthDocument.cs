using System.Text.Json;

namespace CodexTracker.Codex;

// JWT claims are identity hints only. The app-server must confirm the account before a login is saved.
internal sealed record AuthDocument(string Email, string AccountId, string AccessToken, string? PlanType)
{
    public static AuthDocument Parse(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            throw new TrackerException("Cette session ne contient pas de connexion ChatGPT gérée par Codex.");
        var access = Read(tokens, "access_token") ?? throw new TrackerException("La session Codex est incomplète. Reconnectez ce compte.");
        var id = Read(tokens, "id_token");
        using var claims = ParseClaims(id ?? access);
        using var accessClaims = ParseClaims(access);
        var email = Read(claims.RootElement, "email") ?? Nested(accessClaims.RootElement, "https://api.openai.com/profile", "email");
        var accountId = Read(tokens, "account_id") ?? Nested(claims.RootElement, "https://api.openai.com/auth", "chatgpt_account_id")
            ?? Nested(accessClaims.RootElement, "https://api.openai.com/auth", "chatgpt_account_id");
        var plan = Nested(claims.RootElement, "https://api.openai.com/auth", "chatgpt_plan_type")
            ?? Nested(accessClaims.RootElement, "https://api.openai.com/auth", "chatgpt_plan_type");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(accountId))
            throw new TrackerException("L'identité de cette session Codex est indisponible. Reconnectez ce compte.");
        return new(email, accountId, access, plan);
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
