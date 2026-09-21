namespace CodexTracker.Core;

public static class RefreshPolicy
{
    public static int NormalizeMinutes(int minutes) => minutes is 1 or 2 or 5 ? minutes : 2;
    public static TimeSpan Interval(int minutes, bool adaptive, TimeSpan idle) =>
        TimeSpan.FromMinutes(adaptive && idle >= TimeSpan.FromMinutes(5) ? 10 : NormalizeMinutes(minutes));
}
