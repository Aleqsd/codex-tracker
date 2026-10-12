namespace CodexTracker.Core;

public enum ResetKind { Weekly, Short, Reserve }

public sealed record ResetScheduleEntry(AccountState Account, ResetKind Kind, DateTimeOffset? At,
    DateTimeOffset? GrantedAt = null, string? CreditTitle = null, bool IsUndetailedReserve = false, string? CreditId = null, bool IsProjected = false);

public static class ResetSchedule
{
    public static IReadOnlyList<ResetScheduleEntry> Entries(TrackerState state, Guid? accountId = null, DateTimeOffset? now = null)
    {
        var entries = new List<ResetScheduleEntry>();
        foreach (var account in state.Accounts.Where(a => accountId is null || a.Profile.Id == accountId))
        {
            var snapshot = account.Snapshot;
            foreach (var kind in new[] { ResetKind.Weekly, ResetKind.Short })
            {
                var (at, projected) = QuotaPresentation.ShownResetsAt(state, account, kind, now ?? DateTimeOffset.UtcNow);
                entries.Add(new(account, kind, at, IsProjected: projected));
            }
            var credits = snapshot?.ResetCredits;
            if (credits is { Count: > 0 })
                entries.AddRange(credits.Select(c => new ResetScheduleEntry(account, ResetKind.Reserve, c.ExpiresAt, c.GrantedAt, c.Title, CreditId: c.Id)));
            if (account.Profile.Provider == AccountProvider.Codex && ((credits is not { Count: > 0 } && snapshot?.AvailableResetCredits is not 0)
                || snapshot?.AvailableResetCredits > credits?.Count))
                entries.Add(new(account, ResetKind.Reserve, null, IsUndetailedReserve: true));
        }
        return entries.OrderBy(e => e.At ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Account.Profile.Email, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Kind).ToArray();
    }
}
