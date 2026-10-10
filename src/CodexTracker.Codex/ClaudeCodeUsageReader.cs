using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed class ClaudeCodeUsageReader(ClaudeCodeLocation location, ClaudeCodeObservations observations,
    IReadOnlyList<string> desktopPaths, Func<Guid, IReadOnlyList<UsageSample>>? history = null) : IAccountUsageReader
{
    public async Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken)
    {
        var identity = await ClaudeCodeIdentity.ReadAsync(location, cancellationToken);
        if (identity.AccountId != expectedAccountId || !string.Equals(identity.Email, profile.Email, StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(Loc.T("Le compte Claude Code a changé. Le relevé a été ignoré."));
        var desktop = await ClaudeDesktopUsageReader.ReadAsync(desktopPaths, identity, cancellationToken);
        AccountSnapshot? terminal = null;
        try { terminal = observations.Read(identity); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or TrackerException) { }
        var latest = desktop is not null && (terminal is null || desktop.FetchedAt > terminal.FetchedAt) ? desktop : terminal ??
            throw new TrackerException(Loc.T("Quotas Claude en attente. Ouvrez l’application Claude pour un relevé automatique, ou configurez les relevés du terminal dans Réglages → Général."));
        latest = KeepTerminalResets(latest, terminal);
        if (latest.Weekly is { ResetsAt: null })
        {
            var samples = history?.Invoke(profile.Id) ?? [];
            var observed = samples.Where(s => s.WeeklyRemaining is not null)
                .Select(s => (s.Timestamp, 100 - s.WeeklyRemaining!.Value))
                .Concat(await ClaudeDesktopUsageReader.ReadWeeklyAsync(desktopPaths, identity, cancellationToken));
            var anchor = WeeklyResetInference.Anchor(WeeklyResetInference.LatestReset(observed), LastExactWeeklyReset(samples, terminal, latest.FetchedAt));
            latest = WithWeeklyReset(latest, WeeklyResetInference.Next(anchor, latest.FetchedAt));
        }
        return latest;
    }

    /// <summary>A newer Desktop reading has no dates: a terminal date for the same, still running period stays valid.</summary>
    internal static AccountSnapshot KeepTerminalResets(AccountSnapshot latest, AccountSnapshot? terminal)
    {
        if (terminal is null || ReferenceEquals(latest, terminal)) return latest;
        var dated = terminal.Buckets.SelectMany(b => b.Windows).Where(w => w.ResetsAt > latest.FetchedAt).ToArray();
        if (dated.Length == 0) return latest;
        return latest with
        {
            Buckets = latest.Buckets.Select(bucket => bucket with
            {
                Windows = bucket.Windows.Select(window => window.ResetsAt is null && dated.FirstOrDefault(d => d.WindowDurationMins == window.WindowDurationMins) is { } source
                    ? window with { ResetsAt = source.ResetsAt } : window).ToArray()
            }).ToArray()
        };
    }

    /// <summary>A past weekly date given by Claude Code is an exact reset moment; it keeps anchoring the following weeks.</summary>
    internal static DateTimeOffset? LastExactWeeklyReset(IEnumerable<UsageSample> samples, AccountSnapshot? terminal, DateTimeOffset at) =>
        samples.Where(s => !s.WeeklyResetEstimated).Select(s => s.WeeklyResetsAt)
            .Append(terminal?.Weekly is { IsResetEstimated: false } known ? known.ResetsAt : null)
            .Where(reset => reset <= at).Max();

    internal static AccountSnapshot WithWeeklyReset(AccountSnapshot snapshot, WeeklyResetInference.Bracket? next) => next is null ? snapshot : snapshot with
    {
        Buckets = snapshot.Buckets.Select(bucket => bucket with
        {
            Windows = bucket.Windows.Select(window => window.IsWeekly && window.ResetsAt is null
                ? window with { ResetsAt = next.By, EstimatedResetFrom = next.After } : window).ToArray()
        }).ToArray()
    };
}
