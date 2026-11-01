namespace CodexTracker.Core;

public enum PillKind { LowQuota, ExpiringReserve }

/// <param name="Key">Identifies the situation: hiding it lasts until another one (next period, another credit) arises.</param>
public sealed record PillSituation(string Key, PillKind Kind, AccountState Account, ResetKind Window, double? Remaining, DateTimeOffset? At);

/// <summary>
/// What the on-screen pill shows: an account in use at 10 % or less, or a reset credit expiring within 24 hours.
/// Nothing otherwise: the pill is not a permanent widget.
/// </summary>
public static class ScreenPill
{
    public const double Low = AccountSuggestions.Low;
    public static readonly TimeSpan ReserveWindow = TimeSpan.FromHours(24);

    public static IReadOnlyList<PillSituation> Current(TrackerState state, DateTimeOffset now, IEnumerable<string>? hidden = null)
    {
        var dismissed = hidden?.ToHashSet(StringComparer.Ordinal) ?? [];
        var situations = new List<PillSituation>();
        foreach (var account in state.ActiveAccounts)
        {
            // The measured window that is low; a declared or passed reset is not low anymore.
            var low = (account.HasShortWindow ? new[] { ResetKind.Weekly, ResetKind.Short } : [ResetKind.Weekly])
                .Select(kind => (Kind: kind, Remaining: QuotaPresentation.Remaining(state, account, kind, now), At: QuotaPresentation.ResetsAt(state, account, kind, now)))
                .Where(w => w.Remaining <= Low && state.ManualCodexReset?.Applies(account, w.Kind, now) != true && !(w.At <= now))
                .OrderBy(w => w.Remaining).FirstOrDefault();
            if (low.Remaining is { } remaining)
                situations.Add(new($"low/{account.Profile.Id:N}/{low.Kind}/{low.At?.UtcTicks}", PillKind.LowQuota, account, low.Kind, remaining, low.At));
        }
        var expiring = state.Accounts
            .Where(a => a.Profile.Provider == AccountProvider.Codex && a.Snapshot is { AvailableResetCredits: > 0 })
            .SelectMany(a => (a.Snapshot!.ResetCredits ?? []).Where(c => c.ExpiresAt > now && c.ExpiresAt - now <= ReserveWindow).Select(c => (Account: a, Credit: c)))
            .OrderBy(p => p.Credit.ExpiresAt).FirstOrDefault();
        if (expiring.Credit is { } credit)
            situations.Add(new($"reserve/{expiring.Account.Profile.Id:N}/{credit.Id}/{credit.ExpiresAt!.Value.UtcTicks}", PillKind.ExpiringReserve,
                expiring.Account, ResetKind.Reserve, null, credit.ExpiresAt));
        return situations.Where(s => !dismissed.Contains(s.Key)).OrderBy(s => s.Kind).ThenBy(s => s.Remaining ?? 100).ToArray();
    }
}
