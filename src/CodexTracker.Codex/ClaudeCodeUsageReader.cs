using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed class ClaudeCodeUsageReader(ClaudeCodeLocation location, ClaudeCodeObservations observations,
    IReadOnlyList<string> desktopPaths) : IAccountUsageReader
{
    public async Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken)
    {
        var identity = await ClaudeCodeIdentity.ReadAsync(location, cancellationToken);
        if (identity.AccountId != expectedAccountId || !string.Equals(identity.Email, profile.Email, StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("Le compte Claude Code a changé. Le relevé a été ignoré.");
        var desktop = await ClaudeDesktopUsageReader.ReadAsync(desktopPaths, identity, cancellationToken);
        AccountSnapshot? terminal = null;
        try { terminal = observations.Read(identity); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or TrackerException) { }
        return desktop is not null && (terminal is null || desktop.FetchedAt > terminal.FetchedAt) ? desktop : terminal ??
            throw new TrackerException("Quotas Claude en attente. Ouvrez l’application Claude pour un relevé automatique, ou configurez les relevés du terminal dans Réglages → Général.");
    }
}
