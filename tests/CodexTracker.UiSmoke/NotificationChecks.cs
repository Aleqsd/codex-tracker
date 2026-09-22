using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Core;

internal static class NotificationChecks
{
    private static int _checks;
    private static void Check(bool result, string message)
    {
        if (!result) throw new Exception(message);
        _checks++; Console.WriteLine("PASS " + message);
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    public static async Task Run(MainWindow window)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CodexTrackerUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var preferences = new PreferencesStore(dataDirectory: directory);
        var settings = window.Settings;
        var host = new Window { Owner = window, Width = 390, Height = 240, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        try
        {
            await using var service = new DemoTrackerService();
            using var runtime = new ReminderRuntime(service, preferences, false);
            var unavailable = await runtime.TestAsync(ReminderChannel.Windows);
            Check(unavailable.Status == DeliveryStatus.Failed && unavailable.Detail.Contains("n’est pas disponible"),
                "Missing Windows notification callback fails honestly");
            var callbackCount = 0;
            runtime.ShowWindows = _ => { callbackCount++; return new(DeliveryStatus.Accepted, "Test transmis à Windows ; affichage non confirmé."); };
            var runtimeComponent = ReminderSettingsView.WindowsTest(runtime, false);
            host.Content = runtimeComponent; host.Show(); host.UpdateLayout();
            Tree(runtimeComponent).OfType<Button>().Single(b => b.Content?.ToString() == "Tester une notification Windows")
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(50);
            Check(callbackCount == 1 && Tree(runtimeComponent).OfType<TextBlock>().Any(t => t.Text == "Test transmis à Windows ; affichage non confirmé.")
                && runtime.History.Last().Status == DeliveryStatus.Accepted,
                "Shared test control awaits the runtime callback and shows the journaled Windows result");
            runtime.Pause();
            var paused = await runtime.TestAsync(ReminderChannel.Windows);
            Check(paused.Status == DeliveryStatus.Skipped && callbackCount == 1, "Paused runtime does not send a Windows test");
            using var demoRuntime = new ReminderRuntime(service, preferences, true);
            demoRuntime.ShowWindows = _ => throw new InvalidOperationException("Demo must never send");
            Check((await demoRuntime.TestAsync(ReminderChannel.Windows)).Status == DeliveryStatus.Skipped,
                "Demo runtime never calls the Windows adapter");
            var corruptDirectory = Path.Combine(directory, "corrupt"); Directory.CreateDirectory(corruptDirectory);
            File.WriteAllText(Path.Combine(corruptDirectory, "reminder-journal.json"), "{broken");
            using var corruptRuntime = new ReminderRuntime(service, new PreferencesStore(dataDirectory: corruptDirectory), false);
            var corrupt = await corruptRuntime.TestAsync(ReminderChannel.Windows);
            Check(corrupt.Status == DeliveryStatus.Failed && corrupt.Detail.Contains("journal") && !corrupt.Detail.Contains("démonstration"),
                "Unreadable reminder journal reports a real failure instead of a demonstration status");
            File.WriteAllText(Path.Combine(corruptDirectory, "reminder-journal.json"), "null");
            using var invalidRuntime = new ReminderRuntime(service, new PreferencesStore(dataDirectory: corruptDirectory), false);
            Check(invalidRuntime.Error is not null && (await invalidRuntime.TestAsync(ReminderChannel.Windows)).Status == DeliveryStatus.Failed
                && File.ReadAllText(Path.Combine(corruptDirectory, "reminder-journal.json")) == "null",
                "Structurally invalid journal suspends delivery without closing the app or erasing evidence");

            window.ShowSettings();
            foreach (var page in new[] { "Rappels", "Canaux" })
            {
                settings.ShowPage(page); settings.UpdateLayout();
                var test = Tree(settings).OfType<Button>().Single(b => b.Content?.ToString() == "Tester une notification Windows");
                var link = Tree(settings).OfType<Button>().Single(b => b.Content?.ToString() == "Réglages Windows ↗");
                Check(!test.IsEnabled && !link.IsEnabled, $"{page} uses the shared Windows test and disables desktop actions in demo");
                Check(link.ToolTip?.ToString() == "Ouvrir les paramètres des notifications Windows", $"{page} explains the explicit Windows settings action");
            }
            window.OpenPage("Comptes");

            var pending = new TaskCompletionSource<DeliveryResult>();
            var attempts = 0;
            var component = ReminderSettingsView.WindowsTest(() => { attempts++; return pending.Task; }, false);
            host.Content = component; host.Show(); host.UpdateLayout();
            var button = Tree(component).OfType<Button>().Single(b => b.Content?.ToString() == "Tester une notification Windows");
            var result = Tree(component).OfType<TextBlock>().Single(t => t.Visibility == Visibility.Collapsed);
            button.Focus();
            Check(button.IsKeyboardFocused, "Windows notification test is reachable by keyboard");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!button.IsEnabled && result.IsVisible && result.Text.Contains("Vérification"), "Windows test shows progress and disables repeat sends while pending");
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(attempts == 1, "Repeated Windows test activation does not submit twice");
            var detail = "Windows suspend les notifications pendant une présentation. Vérifiez les réglages Windows avant de réessayer.";
            pending.SetResult(new(DeliveryStatus.Skipped, detail));
            await Task.Delay(50); host.UpdateLayout();
            Check(button.IsEnabled && result.Text == detail, "Windows test displays the actual diagnostic inline instead of claiming success");
            Check(result.ActualHeight > result.FontSize * 2 && result.ActualWidth <= component.ActualWidth,
                "Windows diagnostic wraps within a compact settings layout");

            var failure = ReminderSettingsView.WindowsTest(() => Task.FromException<DeliveryResult>(new IOException("fictitious private detail")), false);
            host.Content = failure; host.UpdateLayout();
            var failedButton = Tree(failure).OfType<Button>().Single(b => b.Content?.ToString() == "Tester une notification Windows");
            failedButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(failedButton.IsEnabled && Tree(failure).OfType<TextBlock>().Any(t => t.Text.StartsWith("Test impossible"))
                && !Tree(failure).OfType<TextBlock>().Any(t => t.Text.Contains("fictitious private detail")),
                "Windows test recovers from failure without exposing exception details");

            var demo = ReminderSettingsView.WindowsTest(() => { attempts++; return Task.FromResult(new DeliveryResult(DeliveryStatus.Accepted, "Unused")); }, true);
            host.Content = demo; host.UpdateLayout();
            var demoButton = Tree(demo).OfType<Button>().Single(b => b.Content?.ToString() == "Tester une notification Windows");
            demoButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!demoButton.IsEnabled && attempts == 1, "Demo guards Windows delivery even for programmatic activation");
            Console.WriteLine($"PASS {_checks} Windows notification UI checks");
        }
        finally
        {
            host.Close(); window.OpenPage("Comptes");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
