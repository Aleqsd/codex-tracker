namespace CodexTracker.Core;

/// <summary>A user's dated declaration. It never rewrites a measured snapshot or starts a provider request.</summary>
public sealed record ManualCodexReset(DateTimeOffset At, DateTimeOffset DeclaredAt)
{
    public bool Applies(AccountState account, ResetKind kind, DateTimeOffset now)
    {
        if (account.Profile.Provider != AccountProvider.Codex || kind is not (ResetKind.Weekly or ResetKind.Short) ||
            At > DeclaredAt || DeclaredAt > now || At > now || account.Snapshot?.FetchedAt >= At) return false;
        return now - At < TimeSpan.FromMinutes(kind == ResetKind.Weekly ? 10080 : 300);
    }
}

public static class QuotaPresentation
{
    public static double? Remaining(TrackerState state, AccountState account, ResetKind kind, DateTimeOffset now) =>
        state.ManualCodexReset?.Applies(account, kind, now) == true ? 100 :
        kind == ResetKind.Weekly ? account.Snapshot?.Weekly?.RemainingPercent :
        kind == ResetKind.Short ? account.Snapshot?.Short?.RemainingPercent : null;

    public static DateTimeOffset? ResetsAt(TrackerState state, AccountState account, ResetKind kind, DateTimeOffset now) =>
        state.ManualCodexReset?.Applies(account, kind, now) == true ? null :
        kind == ResetKind.Weekly ? account.Snapshot?.Weekly?.ResetsAt :
        kind == ResetKind.Short ? account.Snapshot?.Short?.ResetsAt : null;
}
