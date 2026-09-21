using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using CodexTracker.App;
using CodexTracker.App.Mcp;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class AssistantChecks
{
    private static int _count;
    private static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); _count++; }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, TrackerControl.Json);
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child; }
    internal static async Task Run(MainWindow window)
    {
        using var control = new TrackerControl(window, true); window.Assistant = control;
        var p = window.Preferences;
        p.Update(p => p with { McpEnabled = false });
        var blocked = false; try { await control.HandleAsync("accounts", Element(new { })); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "Disabled MCP denies private reads");
        p.Update(p => p with { McpEnabled = true });
        foreach (var accept in new[] { false, true })
        {
            var id = Guid.NewGuid().ToString();
            var result = Element(await control.HandleAsync("test", Element(new { requestId = id, expectedRevision = p.Revision, value = new { channel = "Email" } })));
            Check(result.GetProperty("status").GetString() == "pending", "External test waits for local confirmation");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            timer.Tick += (_, _) =>
            {
                var dialog = window.OwnedWindows.OfType<TrackerDialog>().FirstOrDefault(); if (dialog is null) return;
                timer.Stop();
                if (accept) Tree(dialog).OfType<Button>().Single(b => Equals(b.Content, "Confirmer")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                else dialog.Close();
            };
            timer.Start();
            JsonElement status = default;
            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                do
                {
                    await Task.Delay(50);
                    status = Element(await control.HandleAsync("action", Element(new { requestId = id })));
                } while (status.GetProperty("status").GetString() is "pending" or "executing" && DateTimeOffset.UtcNow < deadline);
            }
            finally { timer.Stop(); }
            Check(status.GetProperty("status").GetString() == (accept ? "completed" : "cancelled"), accept ? "Approved demo test uses disabled-send runtime" : "Closing approval window cancels request");
            if (accept) Check(status.GetProperty("detail").GetString()!.Contains("désactivés"), "Demo approval cannot send a real email");
        }
        var pendingId = Guid.NewGuid().ToString();
        await control.HandleAsync("test", Element(new { requestId = pendingId, expectedRevision = p.Revision, value = new { channel = "Sms" } }));
        p.Update(p => p with { McpEnabled = false });
        p.Update(p => p with { McpEnabled = true });
        var cancelled = Element(await control.HandleAsync("action", Element(new { requestId = pendingId })));
        Check(cancelled.GetProperty("status").GetString() == "cancelled", "Disabling MCP cancels pending external sends");
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            p.Update(p => p with { ThemeMode = mode }); window.OpenPage("Assistants"); await Task.Delay(100);
            var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
            settings.UpdateLayout();
            Check(Tree(settings).OfType<CheckBox>().Any(b => Equals(b.Content, "Autoriser les assistants via MCP")), "Assistant controls render in " + mode);
            Check(Tree(settings).OfType<Button>().Any(b => Equals(b.Content, "Copier la configuration MCP")), "MCP configuration accessible by keyboard in " + mode);
            settings.Close();
        }
        window.Assistant = null;
        Console.WriteLine($"PASS {_count} assistant WPF checks");
    }
}
