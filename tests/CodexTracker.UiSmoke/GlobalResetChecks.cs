using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodexTracker.App;
using CodexTracker.Core;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class GlobalResetChecks
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
        PreviewClock.Fixed = DateTimeOffset.UtcNow;
        var demo = new DemoTrackerService(false, false, true);
        var resets = (ResetsView)((TabItem)window.FindName("ResetsTab")).Content;
        try
        {
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            foreach (var compact in new[] { false, true })
            {
                window.Preferences.Update(p => p with { ThemeMode = mode }); await Task.Delay(50);
                window.Width = compact ? 630 : 760; window.Height = compact ? 500 : 620;
                window.OpenPage("Comptes"); model.Update(demo.State); window.UpdateLayout();
                Check(model.HasGlobalReset && model.Accounts.Count(a => a.GlobalAnnouncement is not null) == 3, "Global reset applies only to inactive paid demo accounts");
                Check(model.Active!.WeeklyDisplayNumber == "72%" && !model.Accounts.Single(a => a.Plan == "Free").HasResetEstimate, "Measured active quota and excluded Free plan remain unchanged");
                Check(model.Accounts.First(a => a.GlobalAnnouncement is not null).EstimateHint.Contains("example.com/reset-demo"), "Estimate hover exposes dated source and preserves measurement");
                var show = (Button)window.FindName("ShowExpectedResetButton");
                Check(show.Focus() && show.IsKeyboardFocused, "Global reset action supports keyboard navigation");
                foreach (var dpi in new[] { 96, 144, 192 }) window.SaveScreenshot(Path.GetFullPath($"artifacts/previews/global-accounts-{mode}-{compact}-{dpi}.png"), dpi);
                show.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                resets.Update(demo.State); window.UpdateLayout();
                Check(((TabItem)window.FindName("ResetsTab")).IsSelected, "Global reset action opens the sources in Resets");
                var source = Tree(resets).OfType<Button>().Single(b => b.Content?.ToString() == "Voir la confirmation ↗");
                Check(source.Focus() && source.IsKeyboardFocused && source.ActualWidth >= source.DesiredSize.Width, "Source link is keyboard accessible and not cropped");
                Check(Tree(resets).OfType<TextBlock>().Any(t => t.Text.Contains("anciens relevés")), "Agenda distinguishes old deadlines from global reset evidence");
                foreach (var dpi in new[] { 96, 144, 192 }) window.SaveScreenshot(Path.GetFullPath($"artifacts/previews/global-agenda-{mode}-{compact}-{dpi}.png"), dpi);
                var detail = Tree(resets).OfType<Expander>().Single(e => e.Header?.ToString() == "Détail des comptes (5)");
                detail.IsExpanded = true; window.UpdateLayout();
                Check(Tree(detail).OfType<TextBlock>().Any(t => t.Text.Contains("Hors de la portée")) &&
                    Tree(detail).OfType<TextBlock>().Any(t => t.Text.Contains("Compte actif")), "Account details explain scope exclusions and measured active quota");
                resets.Update(demo.State); window.UpdateLayout();
                Check(Tree(resets).OfType<Expander>().Single(e => e.Header?.ToString() == "Détail des comptes (5)").IsExpanded &&
                    Tree(resets).OfType<Expander>().Single(e => e.Name == "ResetAnnouncements").IsExpanded, "Announcement details stay expanded after collection refresh");
                if (!compact)
                {
                    window.Height = 900; window.UpdateLayout();
                    window.SaveScreenshot(Path.GetFullPath($"artifacts/previews/global-details-{mode}-96.png"), 96);
                }
            }
            PreviewClock.Fixed = PreviewClock.UtcNow.AddHours(24); model.Tick(); resets.Tick(); window.UpdateLayout();
            Check(!model.HasGlobalReset && !Tree(resets).OfType<Border>().Any(b => b.Name == "GlobalResetNotice"), "Source banner expires without a new account read");
            model.Update(demo.State with { GlobalResetFeed = null });
            Check(!model.HasGlobalReset, "Disabling announcement monitoring removes estimates immediately");
            PreviewClock.Fixed = clock ?? DateTimeOffset.UtcNow;
            var recent = demo.State with { Accounts = demo.State.Accounts.Select(a => a with { Snapshot = a.Snapshot! with { FetchedAt = PreviewClock.UtcNow } }).ToArray() };
            resets.Update(recent); window.UpdateLayout();
            Check(Tree(resets).OfType<TextBlock>().Any(t => t.Text.Contains("Relevé plus récent")), "A public announcement remains readable after accounts get newer observations");
            window.OpenPage("Rappels"); window.UpdateLayout();
            var manual = Tree(window.Settings).OfType<Button>().Single(b => b.Content?.ToString() == "Vérifier les annonces");
            Check(!manual.IsEnabled, "Demo cannot fetch public sources through the manual check button");
        }
        finally
        {
            PreviewClock.Fixed = clock; window.Width = width; window.Height = height;
            window.Preferences.Update(p => p with { ThemeMode = theme }); model.Update(service.State); resets.Update(service.State);
            window.OpenPage("Comptes"); await demo.DisposeAsync();
        }
    }
}
