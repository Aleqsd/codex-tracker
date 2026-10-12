namespace CodexTracker.Core;

/// <summary>A Windows notification requested for one specific reset, not a recurring rule.</summary>
public sealed record ResetAlert(Guid AccountId, ResetKind Kind, DateTimeOffset At, DateTimeOffset CreatedAt,
    DateTimeOffset ObservedAt, bool IsEstimated = false, bool Enabled = true);

public static class ResetAlertPlanner
{
    public const int MaximumAlerts = 128;
    public static bool Valid(ResetAlert? alert) => alert is not null && alert.AccountId != Guid.Empty &&
        alert.Kind is ResetKind.Short or ResetKind.Weekly && (!alert.IsEstimated || alert.Kind == ResetKind.Weekly) &&
        alert.ObservedAt >= DateTimeOffset.UnixEpoch && alert.ObservedAt <= alert.CreatedAt && alert.CreatedAt >= DateTimeOffset.UnixEpoch && alert.At > alert.CreatedAt &&
        alert.At - alert.CreatedAt <= TimeSpan.FromMinutes((alert.Kind == ResetKind.Short ? 300 : 10080) + 5);
    public static bool Matches(ResetAlert alert, Guid account, ResetKind kind, DateTimeOffset at) =>
        alert.AccountId == account && alert.Kind == kind && alert.At == at;
    public static ResetAlert[] Normalize(IEnumerable<ResetAlert>? alerts) => (alerts ?? []).Where(Valid)
        .GroupBy(a => (a.AccountId, a.Kind, a.At.UtcTicks)).Select(g => g.Last()).OrderBy(a => a.At).TakeLast(MaximumAlerts).ToArray();

    public static IReadOnlyList<ReminderOccurrence> Due(TrackerState state, IEnumerable<ResetAlert> alerts, DateTimeOffset now)
    {
        var result = new List<ReminderOccurrence>();
        foreach (var alert in Normalize(alerts).Where(a => a.Enabled && a.At <= now && now - a.At < TimeSpan.FromDays(1)))
        {
            var account = state.Accounts.FirstOrDefault(a => a.Profile.Id == alert.AccountId);
            if (account is null || account.Snapshot?.FetchedAt > now || alert.Kind == ResetKind.Short && !account.HasShortWindow) continue;
            if (state.ManualCodexReset is { } manual && manual.DeclaredAt >= alert.CreatedAt && manual.Applies(account, alert.Kind, now)) continue;
            var currentAt = QuotaPresentation.ResetsAt(state, account, alert.Kind, now);
            // A different deadline measured BEFORE the requested reset makes that appointment obsolete.
            // A new reading AFTER it may already describe the following period: still deliver the requested alert.
            if (currentAt is { } changed && changed != alert.At && account.Snapshot is { } snapshot &&
                snapshot.FetchedAt >= alert.CreatedAt && snapshot.FetchedAt < alert.At) continue;
            var identity = CalendarExport.Identity(alert.AccountId, alert.Kind.ToString(), "quota", alert.At);
            result.Add(new("reset-alert/" + identity, alert.AccountId, account.Profile.Email, alert.Kind, null,
                alert.At, alert.ObservedAt, 0, ReminderChannel.Windows, Provider: account.Profile.Provider, IsResetEstimated: alert.IsEstimated));
        }
        return result.OrderBy(r => r.At).ToArray();
    }
}
