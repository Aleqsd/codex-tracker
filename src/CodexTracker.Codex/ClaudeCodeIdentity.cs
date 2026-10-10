using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed record ClaudeCodeLocation(string? ConfigPath, string? CredentialsPath)
{
    public static ClaudeCodeLocation Resolve(string? configDirectory, string userProfile)
    {
        try
        {
            if (configDirectory is not null)
            {
                if (string.IsNullOrWhiteSpace(configDirectory) || !Path.IsPathFullyQualified(configDirectory)) return new(null, null);
                var directory = Path.GetFullPath(configDirectory);
                return new(Path.Combine(directory, ".claude.json"), Path.Combine(directory, ".credentials.json"));
            }
            return new(Path.Combine(userProfile, ".claude.json"), Path.Combine(userProfile, ".claude", ".credentials.json"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(null, null); }
    }
}

// Display metadata only. Tokens never leave the native credential file or enter this record.
internal sealed record ClaudeCodeIdentity(string Email, string AccountId, string? PlanType,
    DateTimeOffset? ExpiresAt, int? PlanMultiplier = null, string? OrganizationName = null, bool HasLocalCredential = false)
{
    internal static async Task<ClaudeCodeIdentity> ReadAsync(ClaudeCodeLocation location, CancellationToken token)
    {
        if (location.ConfigPath is null || location.CredentialsPath is null)
            throw new TrackerException(Loc.T("Le dossier CLAUDE_CONFIG_DIR est invalide. Corrigez cette variable puis relancez le tracker."));
        using var config = await ReadDocumentAsync(location.ConfigPath, token);
        if (config.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (!config.RootElement.TryGetProperty("oauthAccount", out var account) || account.ValueKind != JsonValueKind.Object)
            throw new TrackerException(Loc.T("Aucune identité Claude locale détectée. Ouvrez l’onglet Code de l’application Claude ou connectez-vous dans le terminal."));
        // Desktop supplies its session directly to the embedded CLI. Its selected identity and
        // native usage cache remain usable even when the standalone credential file has no login.
        using var credentials = File.Exists(location.CredentialsPath) ? await ReadDocumentAsync(location.CredentialsPath, token) : null;
        var oauth = credentials?.RootElement is { ValueKind: JsonValueKind.Object } credentialRoot &&
            credentialRoot.TryGetProperty("claudeAiOauth", out var localOauth) && localOauth.ValueKind == JsonValueKind.Object
            ? localOauth : default;
        var email = Text(account, "emailAddress");
        var uuid = Text(account, "accountUuid");
        if (email is null || !ProfileStore.IsEmail(email) || !Guid.TryParse(uuid, out var accountId) || accountId == Guid.Empty)
            throw new TrackerException(Loc.T("L’identité locale Claude Code est incomplète. Ouvrez Claude Code et vérifiez votre connexion."));
        var organization = Text(account, "organizationUuid");
        if (organization is not null && !Guid.TryParse(organization, out _))
            throw new TrackerException(Loc.T("L’identité locale Claude Code est incomplète. Ouvrez Claude Code et vérifiez votre connexion."));
        DateTimeOffset? expires = null;
        if (oauth.ValueKind == JsonValueKind.Object && oauth.TryGetProperty("expiresAt", out var expiry) && expiry.ValueKind != JsonValueKind.Null)
        {
            if (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var milliseconds)) throw new JsonException();
            try { expires = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
            catch (ArgumentOutOfRangeException) { throw new JsonException(); }
        }
        var tier = oauth.ValueKind == JsonValueKind.Object ? Text(oauth, "rateLimitTier") : null;
        tier ??= Text(account, "userRateLimitTier");
        int? multiplier = tier is "default_claude_max_20x" ? 20 : tier is "default_claude_max_5x" ? 5 : null;
        var plan = oauth.ValueKind == JsonValueKind.Object ? Text(oauth, "subscriptionType") : null;
        plan ??= Text(account, "organizationType") switch { "claude_team" => "team", "claude_enterprise" => "enterprise", "claude_max" => "max", "claude_pro" => "pro", _ => null };
        var name = Text(account, "organizationName");
        if (name is { Length: > 200 } || name?.Any(char.IsControl) == true) throw new JsonException();
        name = string.IsNullOrWhiteSpace(name) ? plan is "pro" or "max" ? Loc.T("Personnel") : plan is "team" ? Loc.T("Équipe") : plan is "enterprise" ? Loc.T("Entreprise") : null : name.Trim();
        return new(email.Trim(), accountId.ToString("D") + "/" + organization?.ToLowerInvariant(),
            plan, expires, multiplier, name, oauth.ValueKind == JsonValueKind.Object && Text(oauth, "accessToken") is { Length: > 0 });
    }

    internal static async Task<JsonDocument> ReadDocumentAsync(string path, CancellationToken token)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new JsonException();
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, token);
            if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new JsonException();
            buffer.Write(bytes, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    internal static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
