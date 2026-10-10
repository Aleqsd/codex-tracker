namespace CodexTracker.Core;

/// <summary>
/// Claude restarts its weekly quota at the same moment every week, but its Desktop cache gives no date.
/// A clear drop of weekly usage between two readings brackets one reset; later resets follow every seven days.
/// The projection keeps that uncertainty and is replaced by any reset date actually provided by Claude.
/// </summary>
public static class WeeklyResetInference
{
    public static readonly TimeSpan Period = TimeSpan.FromDays(7);
    /// <summary>A wider gap between readings gives no useful moment.</summary>
    public static readonly TimeSpan MaximumBracket = TimeSpan.FromHours(36);
    private const double MinimumDrop = 10, MaximumUsedAfterReset = 30;

    public sealed record Bracket(DateTimeOffset After, DateTimeOffset By);

    /// <summary>Most recent observed reset: weekly usage fell sharply to a low value between two readings.</summary>
    public static Bracket? LatestReset(IEnumerable<(DateTimeOffset At, double Used)> readings)
    {
        Bracket? latest = null;
        (DateTimeOffset At, double Used)? previous = null;
        foreach (var reading in readings.Where(r => double.IsFinite(r.Used) && r.Used is >= 0 and <= 100).OrderBy(r => r.At))
        {
            if (previous is { } before && reading.At > before.At && reading.At - before.At <= MaximumBracket &&
                before.Used - reading.Used >= MinimumDrop && reading.Used <= MaximumUsedAfterReset)
                latest = new(before.At, reading.At);
            previous = reading;
        }
        return latest;
    }

    /// <summary>The next weekly reset after <paramref name="now"/>, as the same bracket moved by whole weeks.</summary>
    public static Bracket? Next(Bracket? observed, DateTimeOffset now)
    {
        if (observed is null || observed.By > now.AddDays(1)) return null;
        var weeks = Math.Max(1, (long)Math.Ceiling((now - observed.By).Ticks / (double)Period.Ticks));
        if (observed.By + Period * weeks <= now) weeks++;
        return new(observed.After + Period * weeks, observed.By + Period * weeks);
    }
}
