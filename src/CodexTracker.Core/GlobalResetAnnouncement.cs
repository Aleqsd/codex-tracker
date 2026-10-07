namespace CodexTracker.Core;

public enum GlobalResetAccountStatus { Estimated, Active, NewerObservation, NotCovered, InsufficientData, OldObservation, SubscriptionEnded, Expired }

// Public evidence, independent of measured account data. Times describe posts, not backend execution.
public sealed record GlobalResetAnnouncement(string Id, string Author, string SourceUrl, string AnnouncementUrl,
    DateTimeOffset AnnouncedAt, DateTimeOffset ReportedAt, DateTimeOffset VerifiedAt, string[] Plans, ResetKind[] Kinds)
{
    public bool IsCurrent(DateTimeOffset now) => AnnouncedAt <= ReportedAt && ReportedAt <= VerifiedAt && VerifiedAt <= now &&
        ReportedAt - AnnouncedAt <= TimeSpan.FromHours(36) && now - ReportedAt < TimeSpan.FromHours(24);

    public bool Applies(AccountState account, ResetKind kind, DateTimeOffset now)
        => StatusFor(account, kind, now) == GlobalResetAccountStatus.Estimated;

    public DateTimeOffset ExpiresAt(ResetKind kind) => ReportedAt.AddHours(kind == ResetKind.Short ? 5 : 24);

    public GlobalResetAccountStatus StatusFor(AccountState account, ResetKind kind, DateTimeOffset now)
    {
        if (account.Profile.Provider != AccountProvider.Codex) return GlobalResetAccountStatus.NotCovered;
        if (!IsCurrent(now) || now >= ExpiresAt(kind)) return GlobalResetAccountStatus.Expired;
        if (!Kinds.Contains(kind)) return GlobalResetAccountStatus.NotCovered;
        if (account.Snapshot is not { } snapshot || snapshot.FetchedAt > now ||
            !string.Equals(account.Profile.Email, snapshot.Email, StringComparison.OrdinalIgnoreCase) || snapshot.PlanType is not { } plan)
            return GlobalResetAccountStatus.InsufficientData;
        if (!Plans.Contains(plan.ToLowerInvariant())) return GlobalResetAccountStatus.NotCovered;
        if (account.IsActiveInCodex || account.IsRefreshing) return GlobalResetAccountStatus.Active;
        if (snapshot.FetchedAt >= AnnouncedAt) return GlobalResetAccountStatus.NewerObservation;
        if (AnnouncedAt - snapshot.FetchedAt > TimeSpan.FromDays(30)) return GlobalResetAccountStatus.OldObservation;
        if (snapshot.SubscriptionEndsAt is { } end && end <= AnnouncedAt) return GlobalResetAccountStatus.SubscriptionEnded;
        var window = kind == ResetKind.Weekly ? snapshot.Weekly : kind == ResetKind.Short ? snapshot.Short : null;
        return window is not null && double.IsFinite(window.UsedPercent) && window.UsedPercent is >= 0 and <= 100
            ? GlobalResetAccountStatus.Estimated : GlobalResetAccountStatus.InsufficientData;
    }

    public static GlobalResetAnnouncement? For(AccountState account, ResetKind kind, GlobalResetFeedState? feed, DateTimeOffset now) =>
        feed?.Announcements.Where(a => a.Applies(account, kind, now)).OrderByDescending(a => a.ReportedAt).FirstOrDefault();
}

public sealed record GlobalResetFeedState(IReadOnlyList<GlobalResetAnnouncement> Announcements,
    DateTimeOffset? CheckedAt = null, string? Error = null, bool IsChecking = false,
    DateTimeOffset? LastAttemptAt = null, DateTimeOffset? NextCheckAt = null, DateTimeOffset? ManualRetryAt = null);
