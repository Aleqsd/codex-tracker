using System.Reflection;
using System.IO;
using ThemeMode = CodexTracker.App.ThemeMode;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Core;

internal static class ProviderChecks
{
    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexTrackerProviderUi", Guid.NewGuid().ToString("N"));
        var preferences = new PreferencesStore(false, root);
        await using var service = new DemoTrackerService(false, showClaudeCode: true, showManualReset: true);
        var now = DateTimeOffset.UtcNow; preferences.Update(p => p with { ManualCodexReset = new(now.AddMinutes(-30), now) });
        var window = new MainWindow(service, true, preferences, new UpdateService());
        try
        {
            window.Show(); await window.InitializeAsync(); await Task.Delay(50);
            var dashboard = (DashboardViewModel)window.DataContext;
            Check(dashboard.ActiveAccounts.Count == 2 && dashboard.Accounts.Count == 8, "Both providers have separate simultaneously active panels");
            var sameEmail = dashboard.Accounts.Where(a => a.Email == "alex@example.com").ToArray();
            Check(sameEmail.Length == 3 && sameEmail.Select(a => a.ProviderName).Distinct().Count() == 2, "Same email is labelled separately per provider and Claude organization");
            var codex = sameEmail.Single(a => a.ProviderName == "Codex"); var claude = sameEmail.Single(a => a.ProviderName == "Claude Code" && a.IsActive);
            Check(sameEmail.Single(a => a.ProviderName == "Claude Code" && !a.IsActive).RowSubtitle.Contains("Entreprise Exemple") && claude.RowSubtitle.Contains("Personnel"), "Personal and company Claude accounts have distinct labels");
            Check(codex.WeeklyNumber == "100%" && codex.HasManualReset && codex.ManualResetHint.Contains("72%"), "Manual reset shows 100% declared and keeps measured quota in tooltip");
            Check(claude.WeeklyNumber == "43%" && !claude.HasManualReset && !claude.HasReserves, "Codex declaration leaves Claude quotas and unsupported reserves alone");
            foreach (var theme in new[] { ThemeMode.Dark, ThemeMode.Light })
            foreach (var compact in new[] { false, true })
            {
                preferences.Update(p => p with { ThemeMode = theme }); window.Width = compact ? 660 : 760; window.Height = compact ? 500 : 700;
                await Task.Delay(50); window.UpdateLayout();
                var card = (Border)window.FindName("ActiveAccountCard");
                Check(Tree(card).OfType<TextBlock>().Any(t => t.Text == "Compte actif dans Claude Code · Personnel") && Tree(card).OfType<TextBlock>().Any(t => t.Text == "Compte actif dans Codex"), $"Provider panels render in {theme}, compact={compact}");
                var list = (ItemsControl)window.FindName("AccountsList");
                var scroll = Tree(window).OfType<ScrollViewer>().First(s => s.Content is StackPanel p && Tree(p).Contains(list));
                Check(scroll.ViewportHeight > 0 && (!compact || scroll.ScrollableHeight > 0), "Compact dashboard keeps both providers and accounts reachable by scrolling");
                var headers = Tree(list).OfType<Grid>().Where(g => g.Tag?.ToString() == "ProviderSectionHeader" && g.Visibility == Visibility.Visible).ToArray();
                Check(headers.Length == 2 && Tree(headers[0]).OfType<TextBlock>().Any(t => t.Text == "Codex") && Tree(headers[1]).OfType<TextBlock>().Any(t => t.Text == "Claude Code"), "Codex and Claude have distinct sections with provider counts");
                Check(headers.All(h => Tree(h).OfType<Image>().Single().Source is System.Windows.Media.Imaging.BitmapImage), "Both provider sections render their bundled official icons");
                scroll.ScrollToTop(); window.UpdateLayout();
                Ui.SaveScreenshot(window, Path.GetFullPath($"artifacts/previews/providers-{theme}-{(compact ? "compact" : "normal")}.png"), 96);
                scroll.ScrollToBottom(); window.UpdateLayout();
                Ui.SaveScreenshot(window, Path.GetFullPath($"artifacts/previews/providers-{theme}-{(compact ? "compact" : "normal")}-sections.png"), 96);
                scroll.ScrollToTop(); window.UpdateLayout();
            }
            var resets = Field<ResetsView>(window, "_resets");
            window.OpenPage("Resets");
            resets.Update(service.State with { GlobalResetFeed = new([
                new("fixture-reset", "Fictional source", "https://example.com/reset", "https://example.com/scope",
                    now.AddHours(-2), now.AddHours(-1), now, ["plus", "pro"], [ResetKind.Weekly, ResetKind.Short])], now) });
            window.UpdateLayout();
            var announcement = Tree(resets).OfType<Expander>().Single();
            Check(announcement.Header?.ToString() == "Détail des comptes (5)", "Codex public reset notices exclude Claude accounts");
            announcement.IsExpanded = true; window.UpdateLayout();
            Check(Tree(announcement).OfType<TextBlock>().Any(t => t.Text.Contains("reset manuel prioritaire")), "A manual declaration takes priority over public reset estimates");
            resets.Update(service.State); window.OpenPage("Comptes");
            var editor = window.OpenManualCodexReset(); await Task.Delay(30);
            var time = Field<TextBox>(editor, "_time"); var save = Field<Button>(editor, "_save");
            time.Text = "oops"; Check(!save.IsEnabled, "Reset form rejects invalid times before saving");
            time.Text = now.AddMinutes(-30).ToLocalTime().ToString("HH:mm");
            Check(save.IsEnabled && time.Focus(), "Reset form accepts a past local time and supports keyboard focus");
            Check(time.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) && Keyboard.FocusedElement != time, "Reset form supports keyboard tab navigation");
            var declaration = preferences.Current.ManualCodexReset;
            preferences.Update(p => p with { Alert10 = !p.Alert10 }); save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(editor.IsVisible && !save.IsEnabled && preferences.Current.ManualCodexReset == declaration, "A stale reset form cannot overwrite changed preferences"); editor.Close();
            var fresh = window.OpenManualCodexReset(); await Task.Delay(20);
            Tree(fresh).OfType<Button>().Single(b => b.Content?.ToString() == "Annuler la déclaration").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(30);
            Check(preferences.Current.ManualCodexReset is null && codex.WeeklyNumber == "72%" && claude.WeeklyNumber == "43%", "Clearing a declaration immediately restores measured values for Codex and keeps Claude");
            Console.WriteLine("PASS provider and manual-reset WPF checks");
        }
        finally { window.PrepareExit(); window.Close(); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Check(bool result, string description) { if (!result) throw new InvalidOperationException(description); }
}
