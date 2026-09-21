namespace CodexTracker.App;

// Presentation/demo clock only. The real reminder engine keeps its own TimeProvider.
internal static class PreviewClock
{
    internal static DateTimeOffset? Fixed { get; set; }
    internal static DateTimeOffset UtcNow => Fixed ?? DateTimeOffset.UtcNow;
}
