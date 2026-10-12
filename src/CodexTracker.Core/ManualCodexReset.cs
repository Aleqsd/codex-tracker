namespace CodexTracker.Core;

/// <summary>A user's dated declaration. It never rewrites a measured snapshot or starts a provider request.</summary>
public sealed record ManualCodexReset(DateTimeOffset At, DateTimeOffset DeclaredAt)
{
    public bool Applies(AccountState account, ResetKind kind, DateTimeOffset now)
    {
        if (account.Profile.Provider != AccountProvider.Codex || kind is not (ResetKind.Weekly or ResetKind.Short) ||
            At > DeclaredAt || DeclaredAt > now || At > now || account.Snapshot?.FetchedAt >= At) return false;
        return now - At < TimeSpan.FromMinutes(Minutes(kind));
    }
    /// <summary>The window restarts at the declared instant: its next reset, an estimate until a new reading.</summary>
    public DateTimeOffset? ProjectedReset(AccountState account, ResetKind kind, DateTimeOffset now) =>
        Applies(account, kind, now) && (kind == ResetKind.Weekly || account.HasShortWindow) ? At.AddMinutes(Minutes(kind)) : null;
    private static int Minutes(ResetKind kind) => kind == ResetKind.Weekly ? 10080 : 300;
}

public static class QuotaPresentation
{
    public static double? Remaining(TrackerState state, AccountState account, ResetKind kind, DateTimeOffset now) =>
        state.ManualCodexReset?.Applies(account, kind, now) == true ? 100 :
        kind == ResetKind.Weekly ? account.Snapshot?.Weekly?.RemainingPercent :
        kind == ResetKind.Short ? account.Snapshot?.Short?.RemainingPercent : null;

    /// <summary>The measured reset: unknown under a declaration, so never scheduled, exported or used for an alert.</summary>
    public static DateTimeOffset? ResetsAt(TrackerState state, AccountState account, ResetKind kind, DateTimeOffset now) =>
        state.ManualCodexReset?.Applies(account, kind, now) == true ? null :
        kind == ResetKind.Weekly ? account.Snapshot?.Weekly?.ResetsAt :
        kind == ResetKind.Short ? account.Snapshot?.Short?.ResetsAt : null;

    /// <summary><see cref="ResetsAt"/>, or under a declaration the reset projected from it, flagged as an estimate.</summary>
    public static (DateTimeOffset? At, bool Projected) ShownResetsAt(TrackerState state, AccountState account, ResetKind kind, DateTimeOffset now) =>
        state.ManualCodexReset?.ProjectedReset(account, kind, now) is { } projected ? (projected, true) : (ResetsAt(state, account, kind, now), false);
}
