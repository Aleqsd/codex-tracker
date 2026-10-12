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
            window.UpdateLayout();
            var activeCard = (Border)window.FindName("ActiveAccountCard");
            Check(activeCard.IsVisible && activeCard.ActualHeight > 0 && window.FindName("ActiveAccountsSummaryButton") is null,
                "Active-account summaries are shown at the top of the dashboard without a toggle");
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
                TypographyChecks.AssertBundled(window);
                var card = (Border)window.FindName("ActiveAccountCard");
                Check(Tree(card).OfType<TextBlock>().Any(t => t.Text == "Compte actif dans Claude Code · Personnel") && Tree(card).OfType<TextBlock>().Any(t => t.Text == "Compte actif dans Codex"), $"Provider panels render in {theme}, compact={compact}");
                var shortReset = Tree(card).OfType<Grid>().Single(g => g.Tag?.ToString() == "ShortResetMetric" && ReferenceEquals(g.DataContext, claude));
                var weekReset = Tree(card).OfType<Grid>().Single(g => g.Tag?.ToString() == "WeeklyResetMetric" && ReferenceEquals(g.DataContext, claude));
                var claudeSnapshot = service.State.Accounts.Single(a => a.IsActiveInClaudeCode).Snapshot!;
                Check(Tree(shortReset).OfType<TextBlock>().Any(t => t.Text == "Reset 5 h") &&
                    Tree(shortReset).OfType<TextBlock>().Any(t => t.Text == Display.ResetHour(claudeSnapshot.Short!.ResetsAt)) &&
                    Tree(weekReset).OfType<TextBlock>().Any(t => t.Text == "Reset semaine") &&
                    Tree(weekReset).OfType<TextBlock>().Any(t => t.Text == Display.ResetHour(claudeSnapshot.Weekly!.ResetsAt)),
                    "Active Claude panel labels both reset types with their own local hour");
                // The demo weekly reset is four days away: its hour must not read as today's.
                var weeklyHour = claudeSnapshot.Weekly!.ResetsAt!.Value.ToLocalTime().ToString("HH:mm");
                Check(Tree(weekReset).OfType<TextBlock>().Any(t => t.Text.Length > weeklyHour.Length + 1 && t.Text.EndsWith(" " + weeklyHour)) &&
                    !Tree(weekReset).OfType<TextBlock>().Any(t => t.Text == weeklyHour),
                    "A weekly reset on another day names its day next to the hour");
                Check(Tree(shortReset).Concat(Tree(weekReset)).OfType<TextBlock>().All(TextFits),
                    $"Reset hours, labels and countdowns fit {theme}, compact={compact}");
                var codexWeek = Tree(card).OfType<Grid>().Single(g => g.Tag?.ToString() == "WeeklyResetMetric" && ReferenceEquals(g.DataContext, codex));
                Check(Tree(codexWeek).OfType<TextBlock>().Any(t => t.Text == codex.ResetHour && t.Text.StartsWith("≈ ")) &&
                    Tree(codexWeek).OfType<TextBlock>().Any(t => t.Text == codex.ResetCompact && t.Text.StartsWith("≈ ")) && Tree(codexWeek).OfType<TextBlock>().All(TextFits),
                    $"A declared Codex reset shows the estimated next weekly reset in {theme}, compact={compact}");
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
            var quotaRefresh = Tree(window).OfType<Button>().Where(b => b.IsVisible && System.Windows.Automation.AutomationProperties.GetName(b)?.StartsWith("Actualiser les quotas") == true).ToArray();
            Check(quotaRefresh.Length == 1 && System.Windows.Automation.AutomationProperties.GetName(quotaRefresh[0]) == "Actualiser les quotas",
                "Quotas are refreshed from the account toolbar only, not again from the footer");
            var banner = (Border)window.FindName("ManualResetBanner"); var dismissBanner = (Button)window.FindName("DismissManualResetButton");
            var editBanner = Tree(banner).OfType<Button>().Single(b => Equals(b.Content, "Modifier…"));
            // Logical focus: Windows may keep the foreground on the user's own window while the suite runs.
            editBanner.Focus(); editBanner.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Check(banner.IsVisible && (dismissBanner.IsKeyboardFocused || FocusManager.GetFocusedElement(window) == dismissBanner),
                "The declared reset notice is dismissible and reachable after Modifier… with Tab");
            var declared = preferences.Current.ManualCodexReset!;
            dismissBanner.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(50); window.UpdateLayout();
            Check(!banner.IsVisible && preferences.Current.DismissedManualCodexReset == declared.DeclaredAt && preferences.Current.ManualCodexReset == declared &&
                codex.HasManualReset && codex.WeeklyNumber == "100%" && dashboard.HasManualReset,
                "Dismissing the notice hides it but keeps the declaration applied to the accounts");
            Check(FocusManager.GetFocusedElement(window) is UIElement { IsVisible: true } next && next != dismissBanner,
                "Dismissing the notice moves keyboard focus to the next visible control");
            dashboard.Tick(); await service.RefreshAsync(); await Task.Delay(50); window.UpdateLayout();
            Check(!banner.IsVisible && dashboard.HasManualReset && activeCard.IsVisible, "A dismissed notice stays hidden after a refresh");
            var redeclared = DateTimeOffset.UtcNow;
            preferences.Update(p => p with { ManualCodexReset = new(declared.At, redeclared) }); await Task.Delay(50); window.UpdateLayout();
            Check(banner.IsVisible, "A new declaration shows its notice again");
            var refused = false;
            try { window.OpenPage("unknown-fixture-page"); } catch (ArgumentException) { refused = true; }
            Check(refused && activeCard.IsVisible, "Invalid page navigation is refused without hiding the summary");
            var originalState = service.State;
            var withoutDates = originalState with { Accounts = originalState.Accounts.Select(a => !a.IsActiveInClaudeCode ? a : a with {
                Snapshot = a.Snapshot! with { Buckets = a.Snapshot!.Buckets.Select(b => b with {
                    Windows = b.Windows.Select(w => w with { ResetsAt = null }).ToArray() }).ToArray() }
            }).ToArray() };
            dashboard.Update(withoutDates); window.UpdateLayout();
            var unknownRows = Tree((Border)window.FindName("ActiveAccountCard")).OfType<Grid>().Where(g =>
                g.Tag?.ToString() is "ShortResetMetric" or "WeeklyResetMetric" && ReferenceEquals(g.DataContext, claude)).ToArray();
            Check(unknownRows.Length == 2 && unknownRows.All(g => Tree(g).OfType<TextBlock>().Any(t => t.Text == "—") &&
                Tree(g).OfType<TextBlock>().Any(t => t.Text == "Indisponible")),
                "Missing Claude reset dates stay unavailable in both active-panel rows");
            dashboard.Update(originalState); window.UpdateLayout();
            await service.RefreshAsync(); await Task.Delay(50); window.UpdateLayout();
            Check(activeCard.IsVisible, "Account refresh keeps the active-account summary visible");
            var resets = Field<ResetsView>(window, "_resets");
            window.OpenPage("Resets");
            resets.Update(service.State with { GlobalResetFeed = new([
                new("fixture-reset", "Fictional source", "https://example.com/reset", "https://example.com/scope",
                    now.AddHours(-2), now.AddHours(-1), now, ["plus", "pro"], [ResetKind.Weekly, ResetKind.Short])], now) });
            window.UpdateLayout();
            resets.ShowAnnouncements(); window.UpdateLayout();
            var announcement = Tree(resets).OfType<Expander>().Single(e => e.Header?.ToString() == "Détail des comptes (5)");
            Check(announcement.Header?.ToString() == "Détail des comptes (5)", "Codex public reset notices exclude Claude accounts");
            announcement.IsExpanded = true; window.UpdateLayout();
            Check(Tree(announcement).OfType<TextBlock>().Any(t => t.Text.Contains("reset manuel prioritaire")), "A manual declaration takes priority over public reset estimates");
            resets.Update(service.State); window.UpdateLayout();
            var declaredAt = preferences.Current.ManualCodexReset!.At;
            var projectedRows = Tree(resets).OfType<Expander>().Where(e => e.Tag is ResetScheduleEntry { IsProjected: true }).ToArray();
            Check(projectedRows.Length > 0 && projectedRows.All(e => e.Tag is ResetScheduleEntry entry &&
                    entry.At == declaredAt.AddMinutes(entry.Kind == ResetKind.Weekly ? 10080 : 300) && Tree(e).OfType<TextBlock>().Any(t => t.Text.EndsWith(" · estimé"))),
                "Codex accounts without a new reading list a reset estimated from the declared time");
            Check(projectedRows.All(e => Tree(e).OfType<Button>().Where(b => b.Tag?.ToString()?.StartsWith("ResetAlert:") == true).All(b => !b.IsEnabled)),
                "Estimated resets cannot be given a notification until a new reading");
            var notice = Tree(resets).OfType<Border>().Single(b => b.Name == "DeclaredResetNotice");
            var dismissNotice = Tree(notice).OfType<Button>().Single();
            dismissNotice.Focus();
            Check(dismissNotice.IsKeyboardFocused || FocusManager.GetFocusedElement(window) == dismissNotice, "The declared reset notice in Resets can be dismissed with the keyboard");
            dismissNotice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(50); window.UpdateLayout();
            Check(!Tree(resets).OfType<Border>().Any(b => b.Name == "DeclaredResetNotice") && !dashboard.ShowManualResetBanner &&
                preferences.Current.DismissedManualCodexReset == preferences.Current.ManualCodexReset!.DeclaredAt &&
                Tree(resets).OfType<Expander>().Any(e => e.Tag is ResetScheduleEntry { IsProjected: true }),
                "Dismissing it hides both notices while the declaration and its estimates stay applied");
            window.OpenPage("Comptes");
            window.UpdateLayout(); await Task.Delay(400); window.UpdateLayout();
            var declareLinks = Tree(window).OfType<Button>().Where(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Déclarer un reset général Codex").ToArray();
            Check(declareLinks.Count(b => b.IsVisible) == 1 && !Tree(window).OfType<Button>().Any(b => Equals(b.Content, "Reset Codex…")),
                $"The general reset is declared from the Codex section header only, not from the global toolbar ({declareLinks.Length} links, {declareLinks.Count(b => b.IsVisible)} visible)");
            var editor = window.OpenManualCodexReset(); await Task.Delay(30);
            await Task.Delay(450); editor.UpdateLayout();
            Ui.SaveScreenshot(editor, System.IO.Path.GetFullPath("artifacts/previews/manual-reset.png"), 96);
            Check(Tree(editor).OfType<TextBlock>().Any(t => t.Text.Contains("comptes Codex passent à 100 %")), "The reset form shows how many Codex accounts it changes");
            TypographyChecks.AssertBundled(editor);
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
    private static bool TextFits(TextBlock text) => new FormattedText(text.Text, System.Globalization.CultureInfo.GetCultureInfo("fr-FR"),
        text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch), text.FontSize,
        text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip).Width <= text.ActualWidth + 1;
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Check(bool result, string description) { if (!result) throw new InvalidOperationException(description); }
}
