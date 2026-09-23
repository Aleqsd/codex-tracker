using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexTracker.App;
using CodexTracker.Core;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class ExpectedResetChecks
{
    private static void Check(bool result, string message)
    { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    public static async Task Run(MainWindow window, ITrackerService service)
    {
        var clock = PreviewClock.Fixed; var theme = window.Preferences.Current.ThemeMode;
        var width = window.Width; var height = window.Height;
        var model = (DashboardViewModel)window.DataContext;
        var now = DateTimeOffset.UtcNow;
        PreviewClock.Fixed = now;
        var demo = new DemoTrackerService(false, true);
        try
        {
            window.OpenPage("Comptes");
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            foreach (var compact in new[] { false, true })
            {
                window.Preferences.Update(p => p with { ThemeMode = mode }); await Task.Delay(50);
                window.Width = compact ? 630 : 760; window.Height = compact ? 500 : 620;
                model.Update(demo.State); window.UpdateLayout();
                var account = model.Accounts.Single(a => a.HasWeeklyEstimate);
                Check(account.WeeklyDisplayNumber == "≈100%" && account.WeeklyNumber == "18%" && account.WeeklyPercent == 18,
                    "Estimated display retains the measured percentage and progress data");
                Check(account.EstimateHint.Contains("18%") && account.EstimateHint.Contains("UTC") && account.EstimateHint.Contains("ailleurs"),
                    "Estimate tooltip includes observation, exact date, timezone and uncertainty");
                Check(((Border)window.FindName("ExpectedResetsBanner")).IsVisible && model.ExpectedResetsNames.Contains(account.Email),
                    $"Reached-reset banner names the inactive account in {mode}, compact={compact}");
                var number = Tree(window).OfType<TextBlock>().Single(t => t.Text == "≈100%");
                Check(number.IsVisible && number.ActualWidth >= number.DesiredSize.Width && Tree(window).OfType<TextBlock>().Any(t => t.Text == "estimé" && t.IsVisible),
                    "Approximate percentage is visible without clipping and explicitly labelled");
                var refresh = Tree(window).OfType<Button>().First(b => b.ToolTip?.ToString() == "Actualiser et détecter le compte Codex");
                Check(refresh.Focus() && refresh.IsKeyboardFocused, "Account refresh remains keyboard accessible");
                var show = (Button)window.FindName("ShowExpectedResetButton");
                Check(show.Focus() && show.IsKeyboardFocused, "Reached-reset action is keyboard accessible");
                show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); window.UpdateLayout();
                var position = number.TransformToAncestor(window).Transform(new Point());
                Check(position.Y > 0 && position.Y + number.ActualHeight < window.ActualHeight - 34,
                    "Reached-reset action scrolls the estimated quota into the visible area even in a small window");
                foreach (var dpi in new[] { 96, 144, 192 })
                    window.SaveScreenshot(Path.GetFullPath($"artifacts/previews/expected-resets-{mode}-{compact}-{dpi}.png"), dpi);
            }
            var inactive = demo.State.Accounts[1];
            var refreshed = inactive with { Snapshot = inactive.Snapshot! with { FetchedAt = now } };
            model.Update(new([refreshed], null));
            Check(!model.HasExpectedResets && !model.Accounts[0].HasWeeklyEstimate, "A new observation removes the estimate immediately");
            model.Update(new([inactive with { IsActiveInCodex = true }], inactive.Profile.Id));
            Check(!model.HasExpectedResets && model.Active!.WeeklyDisplayNumber == "18%", "An active account never presents an estimated 100 percent");
            model.Update(new([inactive], null)); PreviewClock.Fixed = now.AddDays(7); model.Tick();
            Check(!model.HasExpectedResets, "Stale estimates expire on the presentation timer without another collection");
            PreviewClock.Fixed = now;
            window.ShowResets();
            var resets = (ResetsView)((TabItem)window.FindName("ResetsTab")).Content;
            resets.Update(new([inactive], null)); resets.Tick(); window.UpdateLayout();
            Check(Tree(resets).OfType<TextBlock>().Any(t => t.Text == "≈100 % · à confirmer"), "Reset agenda identifies estimated restoration with uncertainty");
            resets.Update(service.State);
        }
        finally
        {
            PreviewClock.Fixed = clock; window.Width = width; window.Height = height;
            window.Preferences.Update(p => p with { ThemeMode = theme }); model.Update(service.State);
            window.OpenPage("Comptes"); await demo.DisposeAsync();
        }
    }
}
