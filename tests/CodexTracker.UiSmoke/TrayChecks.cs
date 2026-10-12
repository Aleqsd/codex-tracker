using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CodexTracker.App;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class TrayChecks
{
    internal static async Task Run()
    {
        var preferences = new PreferencesStore(false, Path.Combine(Path.GetTempPath(), "CodexTrackerTrayUi", Guid.NewGuid().ToString("N")));
        using var theme = new ThemeManager(preferences);
        await using var service = new DemoTrackerService(false);
        await service.InitializeAsync();
        var opens = 0;
        var peek = new TrayPeekWindow(() => opens++);
        try
        {
            var area = TestScreen.Area;
            var anchor = new System.Drawing.Point(area.Right - 210, area.Bottom - 30);
            var scroll = (ScrollViewer)peek.Content;
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            foreach (var compact in new[] { false, true })
            {
                preferences.Update(p => p with { ThemeMode = mode }); await Task.Delay(30);
                scroll.MaxHeight = compact ? 230 : double.PositiveInfinity;
                peek.Update(service.State, preferences.Current); peek.ShowNear(anchor); await Task.Delay(50);
                var handle = new WindowInteropHelper(peek).Handle;
                Check(GetForegroundWindow() != handle, "Hover preview does not steal keyboard focus");
                Check(WindowsLifecycle.Bounds(handle) is { } bounds && bounds.Width > 0 && bounds.Height > 0 &&
                    bounds.X >= area.Left && bounds.Y >= area.Top && bounds.Right <= area.Right && bounds.Bottom <= area.Bottom,
                    "Hover preview stays within the monitor work area");
                Check(Tree(peek).OfType<TextBlock>().Any(t => t.Text == "alex@example.com" && t.ActualWidth > 0), "Fictional account renders in hover preview");
                TypographyChecks.AssertBundled(peek);
                Check(!compact || scroll.ScrollableHeight > 0, "Compact hover preview keeps the content scrollable");
                foreach (var dpi in new[] { 96, 120, 144, 192 })
                    peek.SaveScreenshot(Path.GetFullPath($"artifacts/previews/tray-{mode}-{(compact ? "compact" : "normal")}-{dpi}.png"), dpi);
                peek.Hide();
            }
            scroll.MaxHeight = double.PositiveInfinity;
            await using (var both = new DemoTrackerService(false, showClaudeCode: true))
            {
                await both.InitializeAsync();
                foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
                {
                    preferences.Update(p => p with { ThemeMode = mode }); await Task.Delay(30);
                    peek.Update(both.State, preferences.Current); peek.ShowNear(anchor); await Task.Delay(50);
                    var shown = Tree(peek).OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToArray();
                    Check(shown.Contains("Compte actif dans Codex") && shown.Contains("Compte actif dans Claude Code"), "Hover preview shows the active Codex and Claude Code accounts together");
                    Check(shown.Count(t => t == "5 heures") == 1, "Codex Pro hides its 5-hour quota while Claude Code keeps it");
                    Check(shown.Count(t => t == "Reset semaine") == 2 && shown.Count(t => t == "Reset 5 h") == 1,
                        "Hover preview distinguishes weekly and five-hour resets and hides unsupported Codex Pro short resets");
                    var claude = both.State.Accounts.Single(a => a.IsActiveInClaudeCode);
                    var shortReset = Tree(peek).OfType<Grid>().Single(g => g.Tag?.ToString() == "ShortResetMetric" && g.IsVisible);
                    Check(Tree(shortReset).OfType<TextBlock>().Any(t => t.Text.StartsWith(Display.ResetHour(claude.Snapshot!.Short!.ResetsAt) + " · ")),
                        "Hover preview displays the Claude five-hour reset hour with its countdown");
                    var weeklyReset = Tree(peek).OfType<Grid>().Where(g => g.Tag?.ToString() == "WeeklyResetMetric" && g.IsVisible).ToArray();
                    Check(weeklyReset.Any(g => Tree(g).OfType<TextBlock>().Any(t => t.Text == Display.ResetTiming(claude.Snapshot!.Weekly!.ResetsAt))) &&
                        Display.ResetHour(claude.Snapshot!.Weekly!.ResetsAt) != claude.Snapshot.Weekly.ResetsAt!.Value.ToLocalTime().ToString("HH:mm"),
                        "Hover preview names the day of a weekly reset several days away");
                    Check(WindowsLifecycle.Bounds(new WindowInteropHelper(peek).Handle) is { } bounds && bounds.Bottom <= area.Bottom && bounds.Y >= area.Top, "Two-account hover preview stays within the work area");
                    peek.SaveScreenshot(Path.GetFullPath($"artifacts/previews/tray-both-{mode}-96.png"), 96);
                    peek.Hide();
                    peek.Update(both.State with { Accounts = [claude] }, preferences.Current); peek.ShowNear(anchor); await Task.Delay(50);
                    peek.SaveScreenshot(Path.GetFullPath($"artifacts/previews/tray-claude-{mode}-96.png"), 96);
                    peek.Hide();
                }
                var unknown = both.State with { Accounts = both.State.Accounts.Where(a => a.IsActiveInClaudeCode).Select(a => a with {
                    Snapshot = a.Snapshot! with { Buckets = a.Snapshot!.Buckets.Select(b => b with {
                        Windows = b.Windows.Select(w => w with { ResetsAt = null }).ToArray() }).ToArray() }
                }).ToArray() };
                peek.Update(unknown, preferences.Current); peek.ShowNear(anchor); await Task.Delay(50);
                var unknownRows = Tree(peek).OfType<Grid>().Where(g => g.Tag?.ToString() is "ShortResetMetric" or "WeeklyResetMetric" && g.IsVisible).ToArray();
                Check(unknownRows.Length == 2 && unknownRows.All(g => Tree(g).OfType<TextBlock>().Any(t => t.Text == "Indisponible")),
                    "Hover preview does not invent hours when Claude reset dates are absent");
                peek.Hide();
            }
            preferences.Update(p => p with { ThemeMode = ThemeMode.Dark }); await Task.Delay(30);
            peek.Update(service.State, preferences.Current); peek.ShowNear(anchor); await Task.Delay(50);
            Check(!Tree(peek).OfType<TextBlock>().Any(t => t.IsVisible && t.Text == "Compte actif dans Claude Code"), "Hover preview drops the second section when one account remains");
            peek.Activate();
            var button = Tree(peek).OfType<Button>().Single();
            scroll.Focus(); scroll.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(button.IsKeyboardFocused, "Hover preview action is reachable by keyboard");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(opens == 1 && !peek.IsVisible, "Hover preview action opens the tracker and hides the popup");
        }
        finally { peek.Close(); }
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Check(bool result, string description)
    { if (!result) throw new InvalidOperationException(description); Console.WriteLine("PASS " + description); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
