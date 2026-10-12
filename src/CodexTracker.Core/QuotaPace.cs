namespace CodexTracker.Core;

/// <summary>Where a quota stands against a steady use that would empty it exactly at its reset.</summary>
/// <param name="Remaining">Measured percentage left.</param>
/// <param name="Expected">Percentage that a steady use since the start of the window would leave now.</param>
/// <param name="Left">Time until the measured reset.</param>
public sealed record QuotaPace(double Remaining, double Expected, TimeSpan Left)
{
    /// <summary>Within this many points of the steady line, the use is considered on pace.</summary>
    public const double Tolerance = 3;
    private static readonly TimeSpan BudgetHorizon = TimeSpan.FromMinutes(10);

    /// <summary>Points ahead of the steady line (positive: more left than needed) or behind it (negative).</summary>
    public double Margin => Remaining - Expected;
    public bool OnPace => Math.Abs(Margin) < Tolerance;

    /// <summary>Share usable per day until the reset, per hour within the last day; none in the last ten minutes.</summary>
    public (double Amount, bool PerHour)? Budget => Left < BudgetHorizon ? null
        : Left < TimeSpan.FromDays(1) ? (Remaining / Left.TotalHours, true) : (Remaining / Left.TotalDays, false);

    /// <summary>
    /// Pace of a measured window of the active account. A declared or inferred reset, an inactive account,
    /// a missing duration or an inconsistent date give none: the line would be invented.
    /// </summary>
    public static QuotaPace? For(TrackerState state, AccountState account, ResetKind kind, DateTimeOffset now)
    {
        if (!account.IsActive || kind == ResetKind.Short && !account.HasShortWindow || state.ManualCodexReset?.Applies(account, kind, now) == true) return null;
        var window = kind switch { ResetKind.Weekly => account.Snapshot?.Weekly, ResetKind.Short => account.Snapshot?.Short, _ => null };
        if (window is not { ResetsAt: { } reset, WindowDurationMins: int minutes } || minutes <= 0 || window.IsResetEstimated) return null;
        var duration = TimeSpan.FromMinutes(minutes);
        var left = reset - now;
        if (left <= TimeSpan.Zero || left > duration) return null;
        return new(window.RemainingPercent, 100 * (left / duration), left);
    }
}
