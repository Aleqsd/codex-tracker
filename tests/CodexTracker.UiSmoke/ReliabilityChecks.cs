using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CodexTracker.App;
using CodexTracker.App.Updates;

internal static class ReliabilityChecks
{
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexTrackerUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        MainWindow? window = null;
        await using var service = new DemoTrackerService();
        using var http = new HttpClient();
        using var updates = new UpdateService("0.9.0", http, Path.Combine(root, "updates", "release-cache.json"));
        try
        {
            var initial = new PreferencesStore(dataDirectory: root);
            initial.Update(p => p with { McpEnabled = true });
            File.WriteAllText(Path.Combine(root, "preferences.json"), "broken");
            var preferences = new PreferencesStore(dataDirectory: root);
            window = new MainWindow(service, true, preferences, updates);
            await window.InitializeAsync(); window.Show(); window.UpdateLayout();
            Check(((Border)window.FindName("RecoveryBanner")).IsVisible && preferences.RecoveryRequired && !preferences.Current.McpEnabled,
                "Recovery is visible on the dashboard and never reactivates the MCP");
            Tree((Border)window.FindName("RecoveryBanner")).OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var settings = window.Settings; settings.UpdateLayout();
            Check(settings.CurrentPage == "Application", "Recovery banner opens the application diagnostics");
            var preview = Tree(settings).OfType<CheckBox>().Single(c => c.Content?.ToString() == "Recevoir aussi les préversions");
            Check(preview.IsChecked == false && !updates.IncludePrereleases, "Stable update channel is the visible default");
            preview.IsChecked = true; preview.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(50);
            Check(preferences.Current.IncludePrereleaseUpdates && updates.IncludePrereleases && preferences.Current.DownloadUpdatesAutomatically,
                "Preview opt-in applies without disabling automatic downloads");
            preview.IsChecked = false; preview.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(50);
            Check(!updates.IncludePrereleases && new PreferencesStore(dataDirectory: root).Current.IncludePrereleaseUpdates == false,
                "Returning to stable persists through restart");
            Tree(settings).OfType<Button>().Single(b => b.Content?.ToString() == "Valider les réglages récupérés")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(50);
            Check(!preferences.RecoveryRequired && !preferences.Current.McpEnabled && preferences.Current.ReminderRules!.All(r => !r.Enabled)
                && !((Border)window.FindName("RecoveryBanner")).IsVisible,
                "Recovery acknowledgement clears the warning without enabling reminders or assistants");
            Exception? dialogError = null;
            _ = settings.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = window.OwnedWindows.OfType<TrackerDialog>().Single();
                try
                {
                    var text = string.Join("\n", Tree(dialog).OfType<TextBlock>().Select(t => t.Text));
                    Check(text.Contains("Codex Tracker 0.9.0") && !text.Contains("@") && !text.Contains(root),
                        "Diagnostic preview contains technical status without accounts or private paths");
                }
                catch (Exception error) { dialogError = error; }
                finally { dialog.Close(); }
            }));
            Tree(settings).OfType<Button>().Single(b => b.Content?.ToString() == "Préparer un diagnostic…")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (dialogError is not null) throw dialogError;
            Console.WriteLine($"PASS {_checks} recovery and update-channel WPF checks");
        }
        finally
        {
            window?.PrepareExit(); window?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
