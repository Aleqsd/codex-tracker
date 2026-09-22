using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CodexTracker.App;

internal static class SettingsNavigationChecks
{
    private static int _checks;
    private static void Check(bool result, string message)
    { if (!result) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal static async Task Run(MainWindow window)
    {
        var tabs = (TabControl)window.FindName("MainTabs");
        var settingsTab = (TabItem)window.FindName("SettingsTab");
        var originalWidth = window.Width; var originalHeight = window.Height;
        var originalError = window.AssistantError;
        try
        {
            window.OpenPage("Canaux"); await Task.Delay(80); window.UpdateLayout();
            var settings = window.Settings;
            Check(tabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString()).SequenceEqual(["Comptes", "Resets", "Réglages"]),
                "Main navigation orders Accounts, Resets and Settings consistently");
            Check(ReferenceEquals(settingsTab.Content, settings) && settingsTab.IsSelected && settings.IsVisible && window.OwnedWindows.Count == 0,
                "Settings use a real embedded control without opening a second window");
            settingsTab.Focus();
            Check(settingsTab.IsKeyboardFocused, "Settings tab header accepts keyboard focus");
            var twilio = Tree(settings).OfType<Expander>().Single(e => Equals(e.Header, "Twilio · SMS et appels"));
            twilio.IsExpanded = true; window.UpdateLayout();
            var draft = Tree(twilio).OfType<TextBox>().First(); var originalDraft = draft.Text;
            draft.Text = "fictitious unsaved draft";
            window.OpenPage("Comptes"); await Task.Delay(50); window.ShowSettings(); await Task.Delay(50);
            Check(ReferenceEquals(settings, window.Settings) && settings.CurrentPage == "Canaux" && draft.Text == "fictitious unsaved draft",
                "Changing main tabs preserves the current settings section and unsaved input");
            draft.BringIntoView(); draft.Focus();
            window.AssistantError = "État fictif : service local momentanément indisponible.";
            await Task.Delay(1150);
            Check(draft.IsKeyboardFocused && draft.Text == "fictitious unsaved draft" && ReferenceEquals(settings, window.Settings),
                "A background health change does not rebuild forms or steal keyboard focus");
            Check(((Border)window.FindName("RecoveryBanner")).IsVisible,
                "A newly reported local error appears without requiring a quota refresh");
            draft.Text = originalDraft;
            window.OpenPage("Application"); await Task.Delay(60);
            Check(Tree(settings).OfType<TextBlock>().Any(t => t.Text == window.AssistantError),
                "Application diagnostics reflect errors that appeared while another section was open");
            window.Width = 660; window.Height = 500; window.UpdateLayout();
            var scroll = (ScrollViewer)typeof(SettingsView).GetField("_pageScroll", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(settings)!;
            Check(scroll.ScrollableHeight > 0 && scroll.ViewportWidth > 300 && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
                "Compact settings keep a readable page and use vertical scrolling");
            scroll.ScrollToEnd(); window.UpdateLayout();
            var quit = Tree(settings).OfType<Button>().Single(b => Equals(b.Content, "Quitter Codex Tracker"));
            var bounds = quit.TransformToAncestor(scroll).TransformBounds(new Rect(quit.RenderSize));
            Check(bounds.Top >= 0 && bounds.Bottom <= scroll.ActualHeight + 1,
                "The last application action remains reachable at the bottom of compact settings");
            var navigation = Tree(settings).OfType<Button>().Single(b => Equals(b.Content, "Général"));
            navigation.Focus(); navigation.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(Keyboard.FocusedElement is Button next && Equals(next.Content, "Rappels"),
                "Keyboard navigation follows the settings sidebar order");
        }
        finally
        {
            window.AssistantError = originalError; window.Settings.RefreshHealth();
            window.Width = originalWidth; window.Height = originalHeight; window.OpenPage("Comptes"); window.UpdateLayout();
        }
        Console.WriteLine($"PASS {_checks} integrated settings navigation checks");
    }
}
