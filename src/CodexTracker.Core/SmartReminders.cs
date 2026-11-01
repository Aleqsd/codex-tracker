namespace CodexTracker.Core;

/// <summary>
/// A refill is news only for a window that was at least half used: reminders before a 5-hour or weekly reset,
/// "probably refilled" notices and "quota refilled" notifications are skipped when the window still had more than
/// half left. Explicit bells, global resets and reset credits are never filtered.
/// </summary>
public static class SmartReminders
{
    /// <summary>Remaining percentage above which a refill brings little.</summary>
    public const double Useful = 50;

    /// <summary>True when a reminder about this occurrence's reset helps: unknown quota counts as useful.</summary>
    public static bool Worth(TrackerState state, ReminderOccurrence occurrence, DateTimeOffset now)
    {
        if (occurrence.Kind is not (ResetKind.Weekly or ResetKind.Short)) return true;
        var account = state.Accounts.FirstOrDefault(a => a.Profile.Id == occurrence.AccountId);
        return account is null || Worth(QuotaPresentation.Remaining(state, account, occurrence.Kind, now));
    }

    /// <summary>True when a window with this much left (measured before the refill) benefits from it.</summary>
    public static bool Worth(double? remainingBefore) => remainingBefore is not > Useful;
}
