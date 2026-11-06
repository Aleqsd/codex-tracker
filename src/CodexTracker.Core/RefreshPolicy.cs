namespace CodexTracker.Core;

public static class RefreshPolicy
{
    public static int NormalizeMinutes(int minutes) => minutes is 1 or 2 or 5 ? minutes : 2;
    public static TimeSpan Interval(int minutes, bool adaptive, TimeSpan idle) =>
        TimeSpan.FromMinutes(adaptive && idle >= TimeSpan.FromMinutes(5) ? 10 : NormalizeMinutes(minutes));

    public static TimeSpan RetryDelay(int failures) => failures <= 0 ? TimeSpan.Zero :
        TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));

    public static TimeSpan EffectiveInterval(TimeSpan configured, TimeSpan active, TimeSpan? sinceActivity) =>
        sinceActivity is { } age && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5) && configured > active
            ? active : configured;
}
