namespace CodexTracker.App;

/// <summary>English text for the WPF interface, keyed by the exact French source text or template.</summary>
internal static partial class EnglishApp
{
    internal static IEnumerable<(string French, string English)> All =>
        Shell.Concat(Dashboard).Concat(Resets).Concat(Settings).Concat(Windows).Concat(Updates);
}
