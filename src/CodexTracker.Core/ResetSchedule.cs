namespace CodexTracker.Core;

public enum ResetKind { Weekly, Short, Reserve }

public sealed record ResetScheduleEntry(AccountState Account, ResetKind Kind, DateTimeOffset? At,
    DateTimeOffset? GrantedAt = null, string? CreditTitle = null, bool IsUndetailedReserve = false);

public static class ResetSchedule
{
    public static IReadOnlyList<ResetScheduleEntry> Entries(TrackerState state, Guid? accountId = null)
    {
        var entries = new List<ResetScheduleEntry>();
        foreach (var account in state.Accounts.Where(a => accountId is null || a.Profile.Id == accountId))
        {
            var snapshot = account.Snapshot;
            entries.Add(new(account, ResetKind.Weekly, snapshot?.Weekly?.ResetsAt));
            entries.Add(new(account, ResetKind.Short, snapshot?.Short?.ResetsAt));
            var credits = snapshot?.ResetCredits;
            if (credits is { Count: > 0 })
                entries.AddRange(credits.Select(c => new ResetScheduleEntry(account, ResetKind.Reserve, c.ExpiresAt, c.GrantedAt, c.Title)));
            if ((credits is not { Count: > 0 } && snapshot?.AvailableResetCredits is not 0)
                || snapshot?.AvailableResetCredits > credits?.Count)
                entries.Add(new(account, ResetKind.Reserve, null, IsUndetailedReserve: true));
        }
        return entries.OrderBy(e => e.At ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Account.Profile.Email, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Kind).ToArray();
    }
}
