namespace CodexTracker.Core;

/// <param name="Estimated">The candidate's quota is inferred: a reset time passed since its last reading, or a declared reset.</param>
public sealed record AccountSuggestion(AccountState Account, double Remaining, bool Estimated);

/// <summary>
/// When the account in use is nearly empty, the other account of the same tool with clearly more quota left.
/// It only advises: the tracker never switches accounts.
/// </summary>
public static class AccountSuggestions
{
    /// <summary>The account in use is "nearly empty" at or below this percentage.</summary>
    public const double Low = 10;
    /// <summary>A candidate must keep at least this much, and this much more than the current account.</summary>
    public const double Minimum = 30, Margin = 20;

    public static AccountSuggestion? For(TrackerState state, AccountState current, DateTimeOffset now)
    {
        if (Available(state, current, now) is not { } left || left.Remaining > Low) return null;
        return state.Accounts
            .Where(a => a.Profile.Id != current.Profile.Id && a.Profile.Provider == current.Profile.Provider && a.Snapshot is not null)
            .Select(a => Available(state, a, now) is { } c ? new AccountSuggestion(a, c.Remaining, c.Estimated) : null)
            .OfType<AccountSuggestion>()
            .Where(s => s.Remaining >= Minimum && s.Remaining >= left.Remaining + Margin)
            // Measured quota before an inferred refill, then the most left, then the freshest reading.
            .OrderBy(s => s.Estimated).ThenByDescending(s => s.Remaining).ThenByDescending(s => s.Account.Snapshot!.FetchedAt)
            .FirstOrDefault();
    }

    /// <summary>The lower of the windows that apply; a window whose reset time has passed counts as full, but estimated.</summary>
    public static (double Remaining, bool Estimated)? Available(TrackerState state, AccountState account, DateTimeOffset now)
    {
        (double, bool)? lowest = null;
        foreach (var kind in account.HasShortWindow ? new[] { ResetKind.Weekly, ResetKind.Short } : [ResetKind.Weekly])
        {
            var measured = QuotaPresentation.Remaining(state, account, kind, now);
            if (measured is null) continue;
            var declared = state.ManualCodexReset?.Applies(account, kind, now) == true;
            var refilled = !declared && QuotaPresentation.ResetsAt(state, account, kind, now) is { } reset && reset <= now;
            (double Remaining, bool Estimated) window = refilled ? (100, true) : (measured.Value, declared);
            if (lowest is null || window.Remaining < lowest.Value.Item1)
                lowest = window;
            else if (window.Remaining == lowest.Value.Item1 && window.Estimated)
                lowest = (lowest.Value.Item1, true);
        }
        return lowest;
    }
}
