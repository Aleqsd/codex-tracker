using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CodexTracker.App;
using CodexTracker.Core;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class TrayChecks
{
    internal static async Task Run()
    {
        SaveIconBoard(Path.GetFullPath("artifacts/previews/tray-icons.png"));
        var preferences = new PreferencesStore(false, Path.Combine(Path.GetTempPath(), "CodexTrackerTrayUi", Guid.NewGuid().ToString("N")));
        using var theme = new ThemeManager(preferences);
        await using var service = new DemoTrackerService(false);
        await service.InitializeAsync();
        var opens = 0;
        var selections = new List<(AccountProvider, ResetKind)>();
        var peek = new TrayPeekWindow(() => opens++, (provider, kind) => selections.Add((provider, kind)));
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
                Check((GetWindowLongPtr(handle, -20) & 0x08000000) != 0, "A click in the hover preview does not activate it");
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
                    var choices = Tree(peek).OfType<RadioButton>().Where(r => r.IsVisible).ToArray();
                    Check(choices.Length == 3 && choices[0].IsChecked == true && choices.Count(r => r.IsChecked == true) == 1 &&
                        shown.Count(t => t == "Dans l’icône") == 1, "Hover preview marks the Codex week drawn in the icon by default");
                    var claudeShort = choices.Single(r => r.Tag?.ToString() == "ShortQuotaChoice");
                    claudeShort.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    Check(selections.LastOrDefault() == (AccountProvider.ClaudeCode, ResetKind.Short), "Clicking the Claude Code 5-hour quota chooses it for the icon");
                    peek.SaveScreenshot(Path.GetFullPath($"artifacts/previews/tray-both-{mode}-96.png"), 96);
                    preferences.Update(p => p with { TrayProvider = AccountProvider.ClaudeCode, TrayWindow = ResetKind.Short });
                    peek.Update(both.State, preferences.Current); await Task.Delay(30);
                    Check(claudeShort.IsChecked == true && choices.Count(r => r.IsChecked == true) == 1 &&
                        Tree(claudeShort).OfType<TextBlock>().Any(t => t.IsVisible && t.Text == "Dans l’icône"), "The icon mark follows the chosen quota");
                    peek.SaveScreenshot(Path.GetFullPath($"artifacts/previews/tray-both-claude-5h-{mode}-96.png"), 96);
                    preferences.Update(p => p with { TrayProvider = AccountProvider.Codex, TrayWindow = ResetKind.Weekly });
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
            peek.Activate(); await Task.Delay(50);
            var button = Tree(peek).OfType<Button>().Single();
            scroll.Focus(); scroll.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(Keyboard.FocusedElement is RadioButton { IsChecked: true }, "Hover preview quota choice is reachable by keyboard");
            for (var i = 0; i < 4 && !button.IsKeyboardFocused; i++)
                ((UIElement)Keyboard.FocusedElement).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(button.IsKeyboardFocused, "Hover preview action is reachable by keyboard");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(opens == 1 && !peek.IsVisible, "Hover preview action opens the tracker and hides the popup");
        }
        finally { peek.Close(); }
    }

    // Real renderer output at each shell size: zoomed pixels for 100-200 %, then actual size on dark and light taskbars.
    private static void SaveIconBoard(string path)
    {
        int[] zoomed = [16, 20, 24, 32], actual = [16, 20, 24, 28, 32, 36, 40, 48, 64];
        (string Number, AccountProvider Provider)[] samples = [("85", AccountProvider.Codex), ("100", AccountProvider.Codex), ("7", AccountProvider.Codex),
            ("63", AccountProvider.ClaudeCode), ("18", AccountProvider.ClaudeCode), ("--", AccountProvider.ClaudeCode)];
        var taskbars = new[] { (Dark: true, Back: System.Drawing.Color.FromArgb(32, 32, 32)), (Dark: false, Back: System.Drawing.Color.FromArgb(243, 243, 243)) };
        const int zoom = 4, gap = 6, pad = 12;
        static System.Drawing.Color State(string number, bool dark) => !int.TryParse(number, out var value) ? System.Drawing.Color.FromArgb(140, 140, 140)
            : value > 20 ? (dark ? System.Drawing.Color.FromArgb(240, 240, 236) : System.Drawing.Color.FromArgb(40, 40, 40))
            : value >= 10 ? (dark ? System.Drawing.Color.FromArgb(199, 170, 117) : System.Drawing.Color.FromArgb(147, 103, 30))
            : dark ? System.Drawing.Color.FromArgb(207, 142, 142) : System.Drawing.Color.FromArgb(178, 68, 68);
        int zoomRow(int size) => 2 * samples.Length * (size * zoom + gap) + 3 * gap;
        int[][] actualRows = [[16, 20, 24, 28, 32], [36, 40, 48, 64]];
        int actualRow(int[] sizes) => sizes.Sum(size => samples.Length * (size + gap) + 3 * gap);
        int width = 2 * pad + Math.Max(zoomed.Max(zoomRow), actualRows.Max(actualRow));
        int height = 2 * pad + zoomed.Sum(size => size * zoom + gap) + 2 * actualRows.Sum(sizes => sizes.Max() + 2 * gap) + gap;
        using var board = new System.Drawing.Bitmap(width, height);
        using var graphics = System.Drawing.Graphics.FromImage(board);
        graphics.Clear(System.Drawing.Color.FromArgb(16, 17, 20));
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        void Tile(int size, string number, AccountProvider provider, bool dark, System.Drawing.Color back, int x, int y, int scale)
        {
            using var icon = TrayIconRenderer.RenderBitmap(number, State(number, dark), dark, size, provider);
            using var brush = new System.Drawing.SolidBrush(back);
            graphics.FillRectangle(brush, x, y, size * scale, size * scale);
            graphics.DrawImage(icon, new System.Drawing.Rectangle(x, y, size * scale, size * scale));
        }
        int top = pad;
        foreach (var size in zoomed)
        {
            int left = pad;
            foreach (var (dark, back) in taskbars)
            {
                foreach (var (number, provider) in samples) { Tile(size, number, provider, dark, back, left, top, zoom); left += size * zoom + gap; }
                left += gap;
            }
            top += size * zoom + gap;
        }
        foreach (var (dark, back) in taskbars)
        foreach (var sizes in actualRows)
        {
            using var band = new System.Drawing.SolidBrush(back);
            graphics.FillRectangle(band, pad, top, actualRow(sizes), sizes.Max() + gap);
            int left = pad + gap;
            foreach (var size in sizes)
            {
                foreach (var (number, provider) in samples) { Tile(size, number, provider, dark, back, left, top + (sizes.Max() + gap - size) / 2, 1); left += size + gap; }
                left += 2 * gap;
            }
            top += sizes.Max() + 2 * gap;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        board.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
}
