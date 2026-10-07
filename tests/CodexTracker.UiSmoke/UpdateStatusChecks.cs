using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexTracker.App;
using CodexTracker.App.Updates;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class UpdateStatusChecks
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    private static T Field<T>(SettingsView view, string name) =>
        (T)typeof(SettingsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(80);
    }

    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexTrackerUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var mode = "success";
        await using var tracker = new DemoTrackerService();
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            if (mode == "offline") throw new HttpRequestException("Fictional offline response");
            if (mode == "limited")
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new(TimeSpan.FromHours(1));
                return limited;
            }
            if (request.Headers.IfNoneMatch.Count > 0) return new(HttpStatusCode.NotModified);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            response.Headers.ETag = new("\"fictional\"");
            return response;
        }));
        using var updates = new UpdateService("0.9.0", client, Path.Combine(root, "cache.json"), () => now);
        using var automatic = new AutomaticUpdater(updates, () => now);
        var preferences = new PreferencesStore(dataDirectory: root);
        MainWindow? owner = null;
        Window? host = null;
        SettingsView? settings = null;
        try
        {
            owner = new MainWindow(tracker, true, preferences, updates);
            await owner.InitializeAsync();
            settings = new SettingsView(owner, preferences, updates, false, automatic);
            settings.ShowPage("Application");
            host = new Window { Content = settings, Width = 760, Height = 700,
                ShowInTaskbar = false, Title = "Mises à jour · données fictives" };
            host.Show(); await Settle(); host.UpdateLayout();
            var status = Field<TextBlock>(settings, "_updateStatus");
            var schedule = Field<TextBlock>(settings, "_automaticStatus");
            var navigation = Tree(settings).OfType<Button>().Single(b => Equals(b.Content, "Application"));
            navigation.Focus();
            automatic.SetEnabled(true);
            await automatic.TickAsync(); await Settle();
            Check(calls == 1 && status.Text.Contains("Aucune version plus récente") &&
                status.Text.Contains(now.ToLocalTime().ToString("dd/MM/yyyy à HH:mm:ss")),
                "Automatic checks refresh Settings without opening a new version or clicking Search");
            var before = status.Text;
            now = now.AddMinutes(15);
            await automatic.TickAsync(); await Settle();
            Check(calls == 2 && status.Text != before && status.Text.Contains("Vérifié le " + now.ToLocalTime().ToString("dd/MM/yyyy à HH:mm:ss")),
                "A 304 automatic revalidation updates the visible successful timestamp");
            Check(navigation.IsKeyboardFocused, "An automatic check preserves keyboard focus and the Settings view");
            // The production timer projects the scheduler, not the earlier five-minute manual deadline.
            await Task.Delay(1100);
            Check(schedule.Text.Contains(automatic.NextCheck.ToLocalTime().ToString("dd/MM/yyyy à HH:mm:ss")) &&
                automatic.NextCheck == now.AddMinutes(15), "Settings displays the actual next automatic check");
            var verifiedAt = now;
            now = automatic.NextCheck; mode = "offline";
            await automatic.TickAsync(); await Settle();
            Check(status.Text.Contains("n’a pas pu") && status.Text.Contains(verifiedAt.ToLocalTime().ToString("dd/MM/yyyy à HH:mm:ss")),
                "An offline automatic check reports the failure and preserves the last successful timestamp");
            now = automatic.NextCheck; mode = "limited";
            await automatic.TickAsync(); await Settle(); await Task.Delay(1100);
            Check(status.Text.Contains("limite temporairement") && automatic.NextCheck == now.AddHours(1) &&
                schedule.Text.Contains(automatic.NextCheck.ToLocalTime().ToString("dd/MM/yyyy à HH:mm:ss")),
                "Settings reflects GitHub backoff instead of promising a check in fifteen minutes");
            now = automatic.NextCheck; mode = "success";
            await automatic.TickAsync(); await Settle();
            Check(status.Text.Contains("Aucune version plus récente") && !status.Text.Contains("limite temporairement"),
                "A successful automatic retry clears the previous failure");
            foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
            foreach (var compact in new[] { false, true })
            {
                preferences.Update(p => p with { ThemeMode = theme });
                host.Width = compact ? 660 : 760; host.Height = compact ? 500 : 700;
                await Settle(); host.UpdateLayout();
                var scroll = Field<ScrollViewer>(settings, "_pageScroll");
                var bounds = schedule.TransformToAncestor(scroll).TransformBounds(new Rect(schedule.RenderSize));
                Check(scroll.ViewportWidth > 300 && bounds.Left >= 0 && bounds.Right <= scroll.ViewportWidth + 1,
                    $"Update schedule fits {theme} Settings, compact={compact}");
                Ui.SaveScreenshot(host, Path.GetFullPath($"artifacts/previews/updates-{theme}-{(compact ? "compact" : "normal")}-120.png"), 120);
            }
            preferences.Update(p => p with { DownloadUpdatesAutomatically = false });
            automatic.SetEnabled(false); await Settle();
            var pausedCalls = calls;
            now = now.AddHours(2); await automatic.TickAsync();
            Check(calls == pausedCalls && schedule.Text == "Recherche automatique désactivée.",
                "Disabling automatic updates is visible and prevents requests");
            var disposedStatus = status.Text;
            settings.Dispose();
            now = now.AddMinutes(15); await updates.CheckDetailedAsync(); await Settle();
            Check(status.Text == disposedStatus, "Disposed Settings no longer receives check notifications");
            Console.WriteLine($"PASS {_checks} automatic update status WPF checks");
        }
        finally
        {
            settings?.Dispose(); host?.Close(); owner?.PrepareExit(); owner?.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(respond(request));
    }
}
