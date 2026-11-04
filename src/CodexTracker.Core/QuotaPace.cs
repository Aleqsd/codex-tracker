namespace CodexTracker.Core;

/// <summary>What the average use since the start of a window leads to at its reset.</summary>
public enum PaceOutlook { Holds, Tight, RunsOut }

/// <summary>Where a quota stands against its reset, from the use measured since the window began.</summary>
/// <param name="Remaining">Measured percentage left.</param>
/// <param name="Expected">Percentage that a steady use since the start of the window would leave now.</param>
/// <param name="Left">Time until the measured reset.</param>
/// <param name="Duration">Length of the window (a week or five hours).</param>
public sealed record QuotaPace(double Remaining, double Expected, TimeSpan Left, TimeSpan Duration)
{
    /// <summary>Within this many points of the steady line, the use is considered on pace.</summary>
    public const double Tolerance = 3;
    /// <summary>Under this many points left at the reset, the quota holds only just.</summary>
    public const double TightMargin = 5;
    /// <summary>A recent-rate exhaustion further away than this is not a warning: the session will have ended.</summary>
    public static readonly TimeSpan ImminentHorizon = TimeSpan.FromHours(6);
    private static readonly TimeSpan BudgetHorizon = TimeSpan.FromMinutes(10);

    /// <summary>Points ahead of the steady line (positive: more left than needed) or behind it (negative).</summary>
    public double Margin => Remaining - Expected;
    public bool OnPace => Math.Abs(Margin) < Tolerance;

    public TimeSpan Elapsed => Duration - Left;
    /// <summary>
    /// The first day of a week (the first hour of a shorter window) counts whole: a morning's work is that day's use,
    /// not a rate to extrapolate over the night.
    /// </summary>
    public TimeSpan MinimumSpan => Duration >= TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
    public bool EarlyInWindow => Elapsed < MinimumSpan;
    /// <summary>Average use since the window began, in points per hour.</summary>
    public double AverageUsePerHour => Math.Max(0, 100 - Remaining) / (Elapsed > MinimumSpan ? Elapsed : MinimumSpan).TotalHours;
    /// <summary>Percentage left at the reset if the average use goes on; negative when the quota runs out first.</summary>
    public double AtReset => Remaining - AverageUsePerHour * Left.TotalHours;
    public PaceOutlook Outlook => Remaining <= 0 || AtReset < 0 ? PaceOutlook.RunsOut : AtReset < TightMargin ? PaceOutlook.Tight : PaceOutlook.Holds;
    /// <summary>When the average use empties the quota before its reset; zero once it is empty.</summary>
    public TimeSpan? TimeToEmpty => Outlook != PaceOutlook.RunsOut ? null
        : Remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromHours(Remaining / AverageUsePerHour);

    /// <summary>Share usable per day until the reset, per hour within the last day; none in the last ten minutes or once empty.</summary>
    public (double Amount, bool PerHour)? Budget => Left < BudgetHorizon || Remaining <= 0 ? null
        : Left < TimeSpan.FromDays(1) ? (Remaining / Left.TotalHours, true) : (Remaining / Left.TotalDays, false);
    /// <summary>The average use in the unit of the budget, to compare both.</summary>
    public double? Average => Budget is not { } budget ? null : budget.PerHour ? AverageUsePerHour : AverageUsePerHour * 24;

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
        return new(window.RemainingPercent, 100 * (left / duration), left, duration);
    }

    /// <summary>
    /// The recent-rate exhaustion worth a warning: still ahead, within a working session, before the reset of its window
    /// (the forecast gives none otherwise). Once reached, the quota itself shows 0 %.
    /// </summary>
    public static bool IsImminent(UsageForecast? forecast, DateTimeOffset now) =>
        forecast is { EstimatedExhaustionAt: { } at, Window: not null } && at > now && at - now <= ImminentHorizon;
}
