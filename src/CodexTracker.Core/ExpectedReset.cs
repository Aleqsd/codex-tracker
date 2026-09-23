namespace CodexTracker.Core;

/// <summary>A dated estimate for an inactive account, never a replacement for its observation.</summary>
public sealed record ExpectedReset(ResetKind Kind, DateTimeOffset At, DateTimeOffset ObservedAt, double LastRemainingPercent)
{
    public static ExpectedReset? For(AccountState account, ResetKind kind, DateTimeOffset now)
    {
        if (account.IsActiveInCodex || account.IsRefreshing || account.Profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(account.Profile.Email) ||
            account.Snapshot is not { } snapshot || snapshot.FetchedAt > now ||
            !string.Equals(snapshot.Email, account.Profile.Email, StringComparison.OrdinalIgnoreCase)) return null;
        var window = kind switch { ResetKind.Weekly => snapshot.Weekly, ResetKind.Short => snapshot.Short, _ => null };
        if (window?.ResetsAt is not { } at || at > now || at <= snapshot.FetchedAt ||
            !double.IsFinite(window.UsedPercent) || window.UsedPercent is < 0 or > 100) return null;
        // Do not extrapolate successive cycles from an increasingly old observation.
        if (now - at >= TimeSpan.FromMinutes(window.WindowDurationMins!.Value)) return null;
        return new(kind, at, snapshot.FetchedAt, window.RemainingPercent);
    }

    public static IReadOnlyList<ReminderOccurrence> Due(TrackerState state, DateTimeOffset now) => state.Accounts
        .SelectMany(account => new[] { ResetKind.Weekly, ResetKind.Short }
            .Select(kind => For(account, kind, now)).OfType<ExpectedReset>()
            // Catch up after sleep, without announcing every old reset on first installation.
            .Where(reset => now - reset.At < TimeSpan.FromDays(1))
            .Select(reset => new ReminderOccurrence(
                $"expected/{CalendarExport.Identity(account.Profile.Id, reset.Kind.ToString(), "quota", reset.At)}",
                account.Profile.Id, account.Profile.Email, reset.Kind, null, reset.At, reset.ObservedAt, 0, ReminderChannel.Windows)))
        .DistinctBy(r => r.Key).OrderBy(r => r.At).ToArray();
}
