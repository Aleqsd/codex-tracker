using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

// Desktop hands its selected account to the embedded CLI without rewriting .claude.json. Its Code tab files
// every session under claude-code-sessions/{account}/{organization}: only these names and file dates are read.
internal static class ClaudeDesktopSelection
{
    private const int MaximumDirectories = 256, MaximumFiles = 1024, MaximumAgentSessions = 32;

    internal sealed record Selection(Guid Account, Guid Organization, DateTimeOffset ActiveAt)
    {
        internal string AccountId => Account.ToString("D") + "/" + Organization.ToString("D");
    }

    /// <summary>Account and organization of the most recently written Code session of the last 30 days.</summary>
    internal static Selection? Read(IReadOnlyList<string> desktopPaths, DateTimeOffset now, CancellationToken token)
    {
        Selection? latest = null;
        foreach (var root in Roots(desktopPaths, "claude-code-sessions"))
            foreach (var (account, accountDirectory) in Children(root))
                foreach (var (organization, directory) in Children(accountDirectory))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var file in directory.EnumerateFiles("local_*.json").Take(MaximumFiles))
                        {
                            var name = Path.GetFileNameWithoutExtension(file.Name);
                            if (name.Length < 7 || !Guid.TryParseExact(name[6..], "D", out _) || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            var at = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                            if (at > now.AddMinutes(1) || at < now.AddDays(-30) || at <= latest?.ActiveAt) continue;
                            latest = new(account, organization, at);
                        }
                    }
                    catch (Exception ex) when (Recoverable(ex)) { }
                }
        return latest;
    }

    /// <summary>
    /// Desktop's cached response of the profile endpoint that Claude Code reads to fill .claude.json. It names
    /// the selected account only when both UUIDs match; display names and dates are not retained.
    /// </summary>
    internal static ClaudeCodeIdentity? ReadProfileIdentity(IReadOnlyList<string> desktopPaths, Selection selection, DateTimeOffset now,
        CancellationToken token)
    {
        ChromiumUsageCache.Response? latest = null;
        foreach (var directory in desktopPaths.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "Cache", "Cache_Data"))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var response = ChromiumUsageCache.Read(directory, "api.anthropic.com", "/api/oauth/profile", now, token);
                if (response is not null && (latest is null || response.ObservedAt > latest.ObservedAt)) latest = response;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException) { }
        }
        if (latest is null) return null;
        try
        {
            using var document = JsonDocument.Parse(ClaudeDesktopCacheUsageReader.Decode(latest.Body, latest.Encoding), new() { MaxDepth = 16 });
            return Profile(document.RootElement, selection);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or InvalidOperationException or ZstdSharp.ZstdException) { return null; }
    }

    internal static ClaudeCodeIdentity? Profile(JsonElement root, Selection selection)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("organization", out var organization) || organization.ValueKind != JsonValueKind.Object ||
            !Guid.TryParse(ClaudeCodeIdentity.Text(account, "uuid"), out var accountId) || accountId != selection.Account ||
            !Guid.TryParse(ClaudeCodeIdentity.Text(organization, "uuid"), out var organizationId) || organizationId != selection.Organization ||
            ClaudeCodeIdentity.Text(account, "email") is not { } email || !ProfileStore.IsEmail(email)) return null;
        var plan = ClaudeCodeIdentity.Plan(ClaudeCodeIdentity.Text(organization, "organization_type"));
        return new(email.Trim(), selection.AccountId, plan, null, ClaudeCodeIdentity.Multiplier(ClaudeCodeIdentity.Text(organization, "rate_limit_tier")),
            ClaudeCodeIdentity.OrganizationLabel(ClaudeCodeIdentity.Text(organization, "name"), plan, email));
    }

    /// <summary>
    /// Desktop agent sessions keep a native CLI configuration per account and organization. The most recent one
    /// of the selected organization is returned, otherwise one of the same account, whose address is still valid.
    /// </summary>
    internal static async Task<ClaudeCodeIdentity?> ReadAgentIdentityAsync(IReadOnlyList<string> desktopPaths, Selection selection,
        CancellationToken token)
    {
        var prefix = selection.Account.ToString("D") + "/";
        ClaudeCodeIdentity? sameAccount = null;
        foreach (var root in Roots(desktopPaths, "local-agent-mode-sessions"))
            foreach (var (_, accountDirectory) in Children(root).Where(c => c.Id == selection.Account))
            {
                foreach (var (_, directory) in Children(accountDirectory).OrderByDescending(c => c.Id == selection.Organization))
                {
                    DirectoryInfo[] sessions;
                    try
                    {
                        sessions = directory.EnumerateDirectories("local_*").Take(MaximumDirectories)
                            .OrderByDescending(d => d.LastWriteTimeUtc).Take(MaximumAgentSessions).ToArray();
                    }
                    catch (Exception ex) when (Recoverable(ex)) { continue; }
                    foreach (var session in sessions)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var config = new FileInfo(Path.Combine(session.FullName, ".claude", ".claude.json"));
                            if (!config.Exists || (config.Attributes & FileAttributes.ReparsePoint) != 0 ||
                                ((session.Attributes | config.Directory!.Attributes) & FileAttributes.ReparsePoint) != 0) continue;
                            using var document = await ClaudeCodeIdentity.ReadDocumentAsync(config.FullName, token);
                            if (ClaudeCodeIdentity.Account(document.RootElement) is not { } metadata) continue;
                            var identity = ClaudeCodeIdentity.FromMetadata(metadata, default);
                            if (identity.AccountId == selection.AccountId) return identity;
                            if (identity.AccountId.StartsWith(prefix, StringComparison.Ordinal)) sameAccount ??= identity;
                        }
                        catch (Exception ex) when (Recoverable(ex) || ex is TrackerException) { }
                    }
                }
            }
        return sameAccount;
    }

    private static IEnumerable<DirectoryInfo> Roots(IReadOnlyList<string> desktopPaths, string name) => desktopPaths
        .Select(p => Path.Combine(Path.GetDirectoryName(p)!, name)).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new DirectoryInfo(p));

    private static IEnumerable<(Guid Id, DirectoryInfo Directory)> Children(DirectoryInfo parent)
    {
        DirectoryInfo[] children;
        try
        {
            if (!parent.Exists || (parent.Attributes & FileAttributes.ReparsePoint) != 0) yield break;
            children = parent.EnumerateDirectories().Take(MaximumDirectories).ToArray();
        }
        catch (Exception ex) when (Recoverable(ex)) { yield break; }
        foreach (var child in children)
            if ((child.Attributes & FileAttributes.ReparsePoint) == 0 && Guid.TryParseExact(child.Name, "D", out var id) && id != Guid.Empty)
                yield return (id, child);
    }

    private static bool Recoverable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or
        ArgumentException or NotSupportedException or System.Security.SecurityException;
}
