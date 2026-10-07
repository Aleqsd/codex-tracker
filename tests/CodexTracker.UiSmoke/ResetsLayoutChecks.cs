using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CodexTracker.App;
using CodexTracker.Core;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class ResetsLayoutChecks
{
    private static int _checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    private static T Field<T>(ResetsView view, string name) =>
        (T)typeof(ResetsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal static async Task Run(MainWindow window)
    {
        var oldClock = PreviewClock.Fixed; var oldTheme = window.Preferences.Current.ThemeMode;
        var width = window.Width; var height = window.Height;
        var tab = (TabItem)window.FindName("ResetsTab"); var previous = tab.Content;
        PreviewClock.Fixed = DateTimeOffset.UtcNow;
        await using var demo = new DemoTrackerService(false, true, false, true);
        var declarations = 0;
        var view = new ResetsView(window.Preferences, _ => { }, () => { }, () => declarations++);
        try
        {
            tab.Content = view; view.Update(demo.State); window.ShowResets(); await Task.Delay(100); window.UpdateLayout();
            var timeline = Field<StackPanel>(view, "_timeline");
            var rows = Tree(timeline).OfType<Expander>().Where(e => e.Tag is ResetScheduleEntry && e.IsVisible).ToArray();
            Check(rows.Length > 0 && rows.All(e => !e.IsExpanded && ((ResetScheduleEntry)e.Tag).Kind != ResetKind.Reserve && ((ResetScheduleEntry)e.Tag).At > PreviewClock.UtcNow),
                "The reset landing view shows upcoming quota resets with secondary details closed");
            Check(!Tree(timeline).OfType<TextBlock>().Any(t => t.IsVisible && (t.Text.Contains("UTC") || t.Text.StartsWith("Reçu le") || t.Text.Contains("relevé il y a"))),
                "Timezone, grant dates and observation age do not crowd the reset landing view");
            Check(Tree(rows[0]).OfType<Image>().Any(i => i.Source is not null), "Reset rows use the bundled official provider icons");
            var first = rows[0];
            var toggle = Tree(first).OfType<ToggleButton>().Single();
            toggle.BringIntoView(); toggle.Focus(); toggle.IsChecked = true; window.UpdateLayout();
            Check(toggle.IsKeyboardFocused && Tree(first).OfType<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("UTC")),
                "Keyboard focus reaches a reset row and its expanded details expose the exact timezone");
            var firstEntry = (ResetScheduleEntry)first.Tag;
            view.Update(demo.State); window.UpdateLayout();
            Check(Tree(timeline).OfType<Expander>().Single(e => e.Tag is ResetScheduleEntry entry && entry == firstEntry).IsExpanded,
                "Open reset details survive an automatic account refresh");
            foreach (var row in Tree(timeline).OfType<Expander>().Where(e => e.Tag is ResetScheduleEntry).ToArray()) row.IsExpanded = false;
            var reserves = Tree(timeline).OfType<Expander>().Single(e => e.Name == "ReserveResets");
            reserves.IsExpanded = true; window.UpdateLayout();
            var reserveRow = Tree(reserves).OfType<Expander>().First(e => e.Tag is ResetScheduleEntry);
            reserveRow.IsExpanded = true; reserveRow.IsExpanded = false;
            view.Update(demo.State); window.UpdateLayout();
            reserves = Tree(timeline).OfType<Expander>().Single(e => e.Name == "ReserveResets");
            Check(reserves.IsExpanded, "Closing one credit's details does not collapse the reserve section after refresh");
            reserves.IsExpanded = false;
            foreach (var theme in new[] { ThemeMode.Dark, ThemeMode.Light })
            foreach (var compact in new[] { false, true })
            {
                window.Preferences.Update(p => p with { ThemeMode = theme });
                window.Width = compact ? 630 : 760; window.Height = compact ? 500 : 620;
                await Task.Delay(80); window.UpdateLayout();
                foreach (var combo in new[] { Field<ComboBox>(view, "_accounts"), Field<ComboBox>(view, "_kinds") })
                {
                    var bounds = combo.TransformToAncestor(window).TransformBounds(new Rect(combo.RenderSize));
                    Check(combo.ActualWidth >= 120 && bounds.Left >= 0 && bounds.Right <= window.ActualWidth,
                        $"Reset filters fit {theme}, compact={compact}");
                }
                Check(Tree(Field<ComboBox>(view, "_kinds")).OfType<TextBlock>().Any(t => t.Text == "Tous les types") &&
                    !Tree(view).OfType<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("ResetTypeChoice")),
                    $"Reset type dropdown displays its label in {theme}, compact={compact}");
                window.SaveScreenshot(Path.GetFullPath($"artifacts/previews/resets-simple-{theme}-{(compact ? "compact" : "normal")}-120.png"), 120);
                view.ShowWeek(); await Task.Delay(80); window.UpdateLayout();
                var days = Tree(view).OfType<Button>().Where(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Échéances du ")).ToArray();
                Check(days.Length == 7 && days.All(b => b.ActualWidth >= 60), $"Seven readable calendar days fit {theme}, compact={compact}");
                window.SaveScreenshot(Path.GetFullPath($"artifacts/previews/resets-week-{theme}-{(compact ? "compact" : "normal")}-120.png"), 120);
                Tree(view).OfType<RadioButton>().Single(r => Equals(r.Content, "Liste")).IsChecked = true;
            }
            view.ShowWeek(); window.UpdateLayout();
            var day = Tree(view).OfType<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Échéances du "));
            day.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.UpdateLayout();
            var selectedDay = Field<DateOnly?>(view, "_day");
            Check(selectedDay is not null && Tree(timeline).OfType<Expander>().Where(e => e.Tag is ResetScheduleEntry && e.IsVisible)
                .All(e => DateOnly.FromDateTime(((ResetScheduleEntry)e.Tag).At!.Value.LocalDateTime) == selectedDay),
                "Selecting a weekday narrows the reset list to that date");
            view.Update(demo.State); Check(Field<DateOnly?>(view, "_day") == selectedDay, "Collection preserves the selected calendar day");
            var accounts = Field<ComboBox>(view, "_accounts");
            accounts.SelectedValue = demo.State.Accounts.First(a => a.Profile.Provider == AccountProvider.ClaudeCode).Profile.Id;
            window.UpdateLayout();
            Check(!Tree(view).OfType<Expander>().Any(e => e.Name == "ReserveResets"), "Claude-only selection does not display Codex reserve controls");
            Tree(view).OfType<Button>().Single(b => Equals(b.Content, "Reset Codex…")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(declarations == 1, "The reset declaration action remains accessible from the new page");
            Console.WriteLine($"PASS {_checks} simplified reset layout WPF checks");
        }
        finally
        {
            tab.Content = previous; PreviewClock.Fixed = oldClock; window.Width = width; window.Height = height;
            window.Preferences.Update(p => p with { ThemeMode = oldTheme }); window.OpenPage("Comptes");
        }
    }
}
