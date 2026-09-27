namespace CodexTracker.Core;

// Public evidence, independent of measured account data. Times describe posts, not backend execution.
public sealed record GlobalResetAnnouncement(string Id, string Author, string SourceUrl, string AnnouncementUrl,
    DateTimeOffset AnnouncedAt, DateTimeOffset ReportedAt, DateTimeOffset VerifiedAt, string[] Plans, ResetKind[] Kinds)
{
    public bool IsCurrent(DateTimeOffset now) => AnnouncedAt <= ReportedAt && ReportedAt <= VerifiedAt && VerifiedAt <= now &&
        ReportedAt - AnnouncedAt <= TimeSpan.FromHours(36) && now - ReportedAt < TimeSpan.FromHours(24);

    public bool Applies(AccountState account, ResetKind kind, DateTimeOffset now)
    {
        if (!IsCurrent(now) || !Kinds.Contains(kind) || account.IsActiveInCodex || account.IsRefreshing || account.Snapshot is not { } snapshot ||
            snapshot.FetchedAt >= AnnouncedAt || AnnouncedAt - snapshot.FetchedAt > TimeSpan.FromDays(30) ||
            !string.Equals(account.Profile.Email, snapshot.Email, StringComparison.OrdinalIgnoreCase) ||
            snapshot.PlanType is not { } plan || !Plans.Contains(plan.ToLowerInvariant()) ||
            snapshot.SubscriptionEndsAt is { } end && end <= AnnouncedAt) return false;
        var window = kind == ResetKind.Weekly ? snapshot.Weekly : kind == ResetKind.Short ? snapshot.Short : null;
        return window is not null && double.IsFinite(window.UsedPercent) && window.UsedPercent is >= 0 and <= 100 &&
            (kind != ResetKind.Short || now - ReportedAt < TimeSpan.FromHours(5));
    }

    public static GlobalResetAnnouncement? For(AccountState account, ResetKind kind, GlobalResetFeedState? feed, DateTimeOffset now) =>
        feed?.Announcements.Where(a => a.Applies(account, kind, now)).OrderByDescending(a => a.ReportedAt).FirstOrDefault();
}

public sealed record GlobalResetFeedState(IReadOnlyList<GlobalResetAnnouncement> Announcements,
    DateTimeOffset? CheckedAt = null, string? Error = null);
