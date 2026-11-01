namespace CodexTracker.Core;

/// <summary>A reset credit that Codex granted recently: announced once, through the reminder journal.</summary>
public static class NewResetCredits
{
    public const string KeyPrefix = "credit-new/";
    /// <summary>Also covers an inactive account observed a few days after the grant, without announcing old credits.</summary>
    public static readonly TimeSpan Recent = TimeSpan.FromDays(7);

    public static IReadOnlyList<ReminderOccurrence> Due(TrackerState state, DateTimeOffset now) => state.Accounts
        .Where(a => a.Profile.Provider == AccountProvider.Codex && a.Profile.Id != Guid.Empty &&
            a.Snapshot is { AvailableResetCredits: > 0 } snapshot && snapshot.FetchedAt <= now &&
            string.Equals(snapshot.Email, a.Profile.Email, StringComparison.OrdinalIgnoreCase))
        .SelectMany(account => (account.Snapshot!.ResetCredits ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Id) && c.GrantedAt is { } granted && granted <= now && now - granted < Recent &&
                c.ExpiresAt is { } expires && expires > now)
            .Select(c => new ReminderOccurrence(KeyPrefix + CalendarExport.Identity(account.Profile.Id, "credit", c.Id, c.ExpiresAt!.Value),
                account.Profile.Id, account.Profile.Email, ResetKind.Reserve, c.Id, c.ExpiresAt!.Value, account.Snapshot.FetchedAt, 0,
                ReminderChannel.Windows, Provider: account.Profile.Provider)))
        .DistinctBy(r => r.Key).OrderBy(r => r.At).ToArray();

    public static bool Is(ReminderOccurrence r) => r.LeadMinutes == 0 && r.Key.StartsWith(KeyPrefix, StringComparison.Ordinal);
    /// <summary>The calendar identity of the credit, shared with the exported event.</summary>
    public static string? CalendarIdentity(ReminderOccurrence r) => Is(r) ? r.Key[KeyPrefix.Length..] : null;
}
