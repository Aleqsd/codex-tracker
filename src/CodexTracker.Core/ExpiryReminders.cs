namespace CodexTracker.Core;

public sealed record ExpiryReminder(string Key, Guid AccountId, DateTimeOffset ExpiresAt, DateTimeOffset ObservedAt);

public static class ExpiryReminders
{
    public static int NormalizeLeadHours(int hours) => hours is 24 or 72 or 168 ? hours : 24;
    public static IReadOnlyList<ExpiryReminder> Due(TrackerState state, DateTimeOffset now, int leadHours, IReadOnlyDictionary<string, DateTimeOffset> sent)
    {
        var due = new List<ExpiryReminder>();
        foreach (var account in state.Accounts)
        {
            if (account.Snapshot is not { AvailableResetCredits: > 0 } snapshot || snapshot.FetchedAt > now) continue;
            foreach (var credit in snapshot.ResetCredits ?? [])
            {
                if (credit.ExpiresAt is not { } at || at <= now || at > now.AddHours(NormalizeLeadHours(leadHours))) continue;
                var key = CalendarExport.Identity(account.Profile.Id, "credit", credit.Id, at);
                if (!sent.ContainsKey(key)) due.Add(new(key, account.Profile.Id, at, snapshot.FetchedAt));
            }
        }
        return due.DistinctBy(r => r.Key).OrderBy(r => r.ExpiresAt).ToArray();
    }
}
