namespace CodexTracker.Core;

/// <summary>
/// "Use a reset credit now": the week is nearly empty and a credit expires before the week renews, so waiting would
/// lose it. Announced once per credit, through the reminder journal.
/// </summary>
public static class ReserveAdvice
{
    public const string KeyPrefix = "credit-use/";
    /// <summary>Weekly quota left (%) under which a credit is worth using.</summary>
    public const double LowWeekly = 15;
    /// <summary>An older reading may describe a week already used up elsewhere, or already renewed.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(12);

    public static IReadOnlyList<ReminderOccurrence> Due(TrackerState state, DateTimeOffset now) => state.Accounts
        .Where(a => a.Profile.Provider == AccountProvider.Codex && a.Profile.Id != Guid.Empty &&
            state.ManualCodexReset?.Applies(a, ResetKind.Weekly, now) != true &&
            a.Snapshot is { AvailableResetCredits: > 0, Weekly: { ResetsAt: { } weekEnds } week } snapshot &&
            weekEnds > now && !week.IsResetEstimated && week.RemainingPercent < LowWeekly &&
            snapshot.FetchedAt <= now && now - snapshot.FetchedAt < Freshness &&
            string.Equals(snapshot.Email, a.Profile.Email, StringComparison.OrdinalIgnoreCase))
        .SelectMany(account =>
        {
            var snapshot = account.Snapshot!;
            // The credit that expires first, if it expires before the week renews: one advice per account at a time.
            var credit = (snapshot.ResetCredits ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Id) && c.ExpiresAt is { } at && at > now && at < snapshot.Weekly!.ResetsAt)
                .OrderBy(c => c.ExpiresAt).FirstOrDefault();
            return credit is null ? [] : new[] { new ReminderOccurrence(KeyPrefix + CalendarExport.Identity(account.Profile.Id, "credit", credit.Id, credit.ExpiresAt!.Value),
                account.Profile.Id, account.Profile.Email, ResetKind.Reserve, credit.Id, credit.ExpiresAt!.Value, snapshot.FetchedAt, 0,
                ReminderChannel.Windows, Provider: account.Profile.Provider) };
        })
        .DistinctBy(r => r.Key).OrderBy(r => r.At).ToArray();

    public static bool Is(ReminderOccurrence r) => r.LeadMinutes == 0 && r.Key.StartsWith(KeyPrefix, StringComparison.Ordinal);
}
