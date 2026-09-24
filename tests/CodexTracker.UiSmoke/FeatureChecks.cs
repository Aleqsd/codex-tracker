using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Core;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class FeatureChecks
{
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Flags)!.GetValue(target)!;
    private static int _checks;
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); _checks++; Console.WriteLine("PASS " + message); }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Color CenterColor(FrameworkElement element)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(element); var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
    public static async Task Run(MainWindow window, ITrackerService service)
    {
        var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexTrackerUiTests"));
        var directory = Path.Combine(testRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"privacyMode\":true}");
        var preferences = new PreferencesStore(dataDirectory: directory);
        var id = service.State.Accounts[0].Profile.Id;
        var theme = window.Theme;
        try
        {
            var mainTabs = (TabControl)window.FindName("MainTabs");
            var resetsTab = (TabItem)window.FindName("ResetsTab");
            var resets = (ResetsView)resetsTab.Content;
            Check(mainTabs.SelectedIndex == 0, "Dashboard opens on Accounts by default");
            var updateBanner = (Border)window.FindName("UpdateBanner");
            var updateButton = (Button)window.FindName("UpdateNowButton");
            Check(!updateBanner.IsVisible, "Update banner takes no space when no update is ready");
            window.PresentUpdate(null, true); window.UpdateLayout();
            Check(updateBanner.IsVisible && !updateButton.IsVisible, "Download state never offers premature installation");
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                window.Preferences.Update(p => p with { ThemeMode = mode }); await Task.Delay(50);
                window.PresentUpdate("0.9.0", false); window.UpdateLayout();
                Check(updateBanner.IsVisible && updateButton.IsVisible && updateButton.ActualWidth > 120,
                    $"Prepared update has a readable action in {mode}");
                updateButton.Focus();
                Check(updateButton.IsKeyboardFocused, "Prepared update action is reachable by keyboard");
                await window.InstallReadyUpdateAsync();
                Check(window.IsVisible && !window.Dispatcher.HasShutdownStarted, "Fictional preview cannot install a real update");
            }
            window.PresentUpdate(null, false);
            mainTabs.SelectedItem = resetsTab; await Task.Delay(100);
            Check(resets.IsVisible && Tree(resets).OfType<TextBlock>().Any(t => t.Text.Contains("À venir")), "Resets tab renders a chronological schedule");
            var resetFilter = Field<ComboBox>(resets, "_accounts");
            Check(Tree(resetFilter).OfType<TextBlock>().Any(t => t.Text == "Tous les comptes")
                && !Tree(resetFilter).OfType<TextBlock>().Any(t => t.Text.Contains("ResetAccountChoice")), "Resets dropdown displays its label instead of its data type");
            resetFilter.SelectedIndex = 1; window.UpdateLayout();
            Check(Tree(resets).OfType<TextBlock>().Any(t => t.Text == service.State.Accounts[0].Profile.Email)
                && !Tree(resets).OfType<TextBlock>().Any(t => t.Text == service.State.Accounts[1].Profile.Email), "Resets account filter isolates schedule rows");
            resets.Update(service.State);
            Check(resetFilter.SelectedIndex == 1, "Refresh preserves the resets account filter");
            var kindFilters = Tree(resets).OfType<RadioButton>().Where(r => r.GroupName == "ResetKinds").ToArray();
            var weeklyFilter = kindFilters.Single(r => r.Tag is ResetKind.Weekly);
            weeklyFilter.IsChecked = true; window.UpdateLayout();
            var timeline = Field<StackPanel>(resets, "_timeline");
            var weeklyLabels = Tree(timeline).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check(weeklyLabels.Contains("Reset hebdomadaire") && !weeklyLabels.Contains("Reset 5 heures") && !weeklyLabels.Contains("Expiration de réserve"), "Weekly filter shows only weekly resets for the selected account");
            resets.Update(service.State);
            Check(weeklyFilter.IsChecked == true && resetFilter.SelectedIndex == 1, "Refresh preserves both account and reset type filters");
            kindFilters.Single(r => r.Tag is ResetKind.Short).IsChecked = true; window.UpdateLayout();
            var shortLabels = Tree(timeline).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check(shortLabels.Contains("Reset 5 heures") && !shortLabels.Contains("Reset hebdomadaire"), "Five-hour filter uses an explicit reset type label");
            kindFilters.Single(r => r.Tag is ResetKind.Reserve).IsChecked = true; window.UpdateLayout();
            var reserveLabels = Tree(timeline).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check(reserveLabels.Contains("Expiration de réserve") && reserveLabels.Contains("Réserves sans date")
                && reserveLabels.Contains(service.State.Accounts[0].Snapshot!.ResetCredits![0].Title)
                && reserveLabels.Any(t => t.StartsWith("Reçu le ")) && reserveLabels.Any(t => t.Contains("UTC")), "Reserve filter shows source credit title, expiry, received dates, timezone and undetailed reserves");
            resets.Update(new([service.State.Accounts[0] with { Snapshot = service.State.Accounts[0].Snapshot! with { AvailableResetCredits = 0, ResetCredits = [] } }], id));
            window.UpdateLayout();
            Check(Tree(timeline).OfType<TextBlock>().Any(t => t.Text == "Aucune réserve à afficher pour cette sélection."), "Empty reserve filter never falls back to quota resets");
            kindFilters.Single(r => r.Tag is null).IsChecked = true;
            resets.Update(service.State);
            var oldSnapshot = service.State.Accounts[0].Snapshot! with
            {
                Buckets = [new("codex", null, [new(80, 300, DateTimeOffset.UtcNow.AddSeconds(-1))])],
                AvailableResetCredits = null, ResetCredits = null
            };
            resets.Update(new([service.State.Accounts[0] with { Snapshot = oldSnapshot }], id));
            window.UpdateLayout(); resets.Tick();
            var resetLabels = Tree(resets).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check(resetLabels.Contains("Reset à confirmer dans Codex") && resetLabels.Contains("Date indisponible")
                && resetLabels.Contains("Réserves : non communiquées"), "Past reset dates and missing reserves never imply restored quotas or zero credits");
            Guid? exportedAccount = null;
            var scoped = new ResetsView(preferences, selected => exportedAccount = selected, () => { });
            scoped.Update(service.State); Field<ComboBox>(scoped, "_accounts").SelectedIndex = 1;
            Tree(scoped.Content as DependencyObject ?? scoped).OfType<Button>().Single(b => b.Content?.ToString() == "Google Agenda ↗").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(exportedAccount == id, "Calendar action receives the account selected in Resets");
            resets.Update(service.State); resets.ShowWeek(); window.UpdateLayout();
            Check(Tree(resets).OfType<Border>().Count(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Échéances du ")) == 7,
                "Week mode renders seven calendar days with accessible date names");
            Check(Tree(resets).OfType<TextBlock>().Any(t => t.Text == "Réserve prioritaire"),
                "Known available credit has a priority summary outside the calendar range");
            var nextWeek = Tree(resets).OfType<Button>().Single(b => b.ToolTip?.ToString() == "Semaine suivante");
            var before = Field<DateOnly>(resets, "_week"); nextWeek.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); window.UpdateLayout();
            Check(Field<DateOnly>(resets, "_week") == before.AddDays(7) && resetFilter.SelectedIndex == 1,
                "Week navigation retains the selected account");
            resets.Update(service.State); window.UpdateLayout();
            Check(Field<DateOnly>(resets, "_week") == before.AddDays(7) && Field<RadioButton>(resets, "_weekChoice").IsChecked == true,
                "Refresh retains the selected week and display mode");
            resets.Update(new([service.State.Accounts[0] with { Snapshot = oldSnapshot }], id)); window.UpdateLayout();
            Check(Tree(resets).OfType<TextBlock>().Any(t => t.Text.Contains("Dates non communiquées")), "Unknown dates remain available below the week grid");
            Tree(resets).OfType<RadioButton>().Single(r => r.Content?.ToString() == "Agenda").IsChecked = true;
            resets.Update(service.State); resetFilter.SelectedIndex = 0;
            mainTabs.SelectedIndex = 0; await Task.Delay(100);
            ((Button)window.FindName("MinimizeButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(window.WindowState == WindowState.Minimized && window.ShowInTaskbar && window.IsVisible,
                "Minimize keeps the window visible in the Windows taskbar");
            window.ShowPanel();
            Check(window.WindowState == WindowState.Normal && window.IsVisible, "Tray activation restores a minimized window");
            var hideToTray = (Button)window.FindName("HideToTrayButton");
            Check(hideToTray.IsVisible && hideToTray.ActualWidth >= 30 && hideToTray.ToolTip?.ToString()?.Contains("barre d’état") == true,
                "Dedicated status-bar control is visible next to minimize");
            hideToTray.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!window.IsVisible && !window.ShowInTaskbar && !window.Dispatcher.HasShutdownStarted,
                "Tray control hides the window and taskbar entry without exiting");
            window.ShowPanel();
            Check(window.IsVisible && window.ShowInTaskbar && window.WindowState == WindowState.Normal,
                "Reopening from tray restores the window and taskbar entry");
            Check(window.FindName("FooterHideToTrayButton") is null &&
                !Tree(window).OfType<TextBlock>().Any(text => text.Text is "Local & privé" or "Masquer dans la barre d’état"),
                "Footer stays minimal without privacy or tray labels");
            Check(!resets.IsVisible && Tree(window).OfType<Button>().Any(b => b.ToolTip?.ToString() == "Changer le nom ou l’avatar"), "Returning to Accounts preserves the dashboard");
            Check(AccountAvatar.Initials("Alexandre Almeida") == "AA" && AccountAvatar.Initials("alexandre.almeida@example.test") == "AA" && AccountAvatar.Initials("Studio") == "ST" && AccountAvatar.Initials("") == "?", "Default avatars derive initials from names and email addresses");
            Check(AccountAvatar.Background(id).ToString() == AccountAvatar.Background(Guid.Parse(id.ToString())).ToString(), "Avatar color remains stable for an account");
            var source = Path.Combine(directory, "source.png");
            var pixels = Enumerable.Repeat((byte)100, 20 * 12 * 4).ToArray();
            var bitmap = BitmapSource.Create(20, 12, 96, 96, PixelFormats.Bgra32, null, pixels, 20 * 4); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(source)) encoder.Save(stream);
            var photo = AvatarStore.ReadImage(source);
            Check(photo.PixelWidth == photo.PixelHeight, "Avatar import crops to a square");
            var editor = new AccountAppearanceWindow(window, preferences, id, theme); editor.Show();
            Field<TextBox>(editor, "_name").Text = "Studio";
            Check(Field<Border>(editor, "_preview").Child is TextBlock initials && initials.Text == "ST" && Field<Border>(editor, "_preview").CornerRadius.TopLeft == 32, "Personalization preview shows live initials in a round avatar");
            typeof(AccountAppearanceWindow).GetField("_image", Flags)!.SetValue(editor, photo);
            typeof(AccountAppearanceWindow).GetField("_imageChanged", Flags)!.SetValue(editor, true);
            Field<Button>(editor, "_save").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var persisted = new PreferencesStore(dataDirectory: directory);
            var appearance = persisted.Current.Appearances[id]; File.Delete(source);
            Check(appearance.Name == "Studio" && AvatarStore.Load(directory, appearance.AvatarFile) is not null, "Name and copied avatar survive restart and source removal");
            var vm = new AccountViewModel(service.State.Accounts[0], service.State, preferences);
            Check(vm.Email == "Studio" && vm.HasAvatar, "Account names and photos stay visible despite the old privacy setting");
            Check(AvatarStore.Load(directory, "../source.png") is null, "Avatar lookup cannot escape its local directory");
            var other = new AccountViewModel(service.State.Accounts[1], service.State, preferences);
            Check(!other.HasAvatar && other.Email != "Studio", "Avatar and alias remain isolated between accounts");
            var cancelled = new AccountAppearanceWindow(window, preferences, id, theme); cancelled.Show();
            Field<TextBox>(cancelled, "_name").Text = "Discarded"; cancelled.Close();
            Check(preferences.Current.Appearances[id].Name == "Studio", "Closing personalization discards the unsaved name");

            Check(window.FindName("PrivacyButton") is null && !Tree(window).OfType<Button>().Any(b => b.ToolTip?.ToString()?.Contains("Afficher dans l’icône") == true),
                "Dashboard no longer exposes privacy or account selection controls");
            var inactive = service.State with { Accounts = service.State.Accounts.Select(a => a with { IsActiveInCodex = false }).ToArray() };
            var dashboard = new DashboardViewModel(true, preferences); dashboard.Update(inactive);
            Check(inactive.ActiveAccount is null && inactive.SelectedAccount is null && dashboard.Active is null,
                "Missing active account never falls back to the formerly selected account");
            window.ShowSettings(); await Task.Delay(50); window.UpdateLayout();
            var settings = window.Settings; var settingsPreferences = window.Preferences;
            Check(settings.CurrentPage == "Général" && Tree(settings).OfType<Button>().Any(b => b.Content?.ToString() == "Rappels"), "Settings open on General with section navigation");
            Check(mainTabs.Items.Count == 3 && mainTabs.SelectedIndex == 2 && settings.IsVisible && window.FindName("SettingsButton") is null,
                "Settings are the third main tab and the duplicate title-bar button is removed");
            settings.ShowPage("Application"); await Task.Delay(50);
            var autoDownload = Tree(settings).OfType<CheckBox>().Single(c => c.Content?.ToString() == "Télécharger automatiquement les mises à jour");
            var autoInstall = Tree(settings).OfType<CheckBox>().Single(c => c.Content?.ToString() == "Installer au prochain démarrage du tracker");
            autoDownload.IsChecked = false; autoDownload.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!settingsPreferences.Current.DownloadUpdatesAutomatically && settingsPreferences.Current.InstallUpdatesAtStartup,
                "Disabling automatic downloads preserves the independent startup preference");
            autoInstall.IsChecked = false; autoInstall.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!settingsPreferences.Current.InstallUpdatesAtStartup && !settingsPreferences.Current.DownloadUpdatesAutomatically,
                "Automatic update controls persist independently through application commands");
            settings.ShowPage("Général"); await Task.Delay(50);
            Field<ComboBox>(settings, "_refreshSelector").SelectedValue = 1;
            window.UpdateLayout();
            var adaptive = Tree(settings).OfType<CheckBox>().Single(c => c.Content?.ToString() == "Adapter à mon activité");
            adaptive.IsChecked = true; adaptive.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(100);
            Check(settingsPreferences.Current.RefreshMinutes == 1 && settingsPreferences.Current.AdaptiveRefresh,
                "Refresh and adaptive mode save from actual controls");
            var labels = Tree(settings).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check(labels.Contains("Chaque minute") && !labels.Any(t => t.Contains("NumberChoice")), "Refresh selector displays a readable label");
            Tree(settings).OfType<Button>().Single(b => b.Content?.ToString() == "Rappels").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100);
            Check(settings.CurrentPage == "Rappels" && Tree(settings).OfType<Expander>().Any(t => t.Header?.ToString() == "Reset hebdomadaire"), "Reminder settings show per-type expandable rules");
            var week = Tree(settings).OfType<Expander>().Single(t => t.Header?.ToString() == "Reset hebdomadaire"); week.IsExpanded = true; settings.UpdateLayout();
            var smsOneHour = Tree(week).OfType<CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Reset hebdomadaire, 1 h, SMS");
            smsOneHour.IsChecked = true;
            Tree(week).OfType<Button>().Single(b => b.Content?.ToString() == "Enregistrer ces rappels").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(settingsPreferences.Current.ReminderRules!.Any(r => r.Kind == ResetKind.Weekly && r.LeadMinutes.Contains(60) && r.Channels.Contains(ReminderChannel.Sms))
                && !settingsPreferences.Current.ReminderRules!.Any(r => r.Kind == ResetKind.Weekly && r.LeadMinutes.Contains(1440) && r.Channels.Contains(ReminderChannel.Sms)), "Actual controls persist independent channels per lead");
            settings.ShowPage("Canaux"); await Task.Delay(100);
            var twilio = Tree(settings).OfType<Expander>().Single(e => e.Header?.ToString() == "Twilio · SMS et appels"); twilio.IsExpanded = true; settings.UpdateLayout();
            Check(Tree(twilio).OfType<PasswordBox>().Count() == 1 && Tree(twilio).OfType<Button>().Where(b => b.Content?.ToString()?.Contains("test") == true).All(b => !b.IsEnabled), "Twilio secrets use a masked field and demo cannot send real tests");
            var limits = Tree(settings).OfType<Expander>().Single(e => e.Header?.ToString() == "Limites et heures silencieuses"); limits.IsExpanded = true; settings.UpdateLayout();
            var zone = Tree(limits).OfType<ComboBox>().Single();
            var pageScroll = Field<ScrollViewer>(settings, "_pageScroll");
            // Expansion queues Loaded and layout work; the timezone is below the expanded Twilio form.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            zone.BringIntoView(); zone.Focus();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var zoneBounds = zone.TransformToAncestor(pageScroll).TransformBounds(new Rect(zone.RenderSize));
            Check(zone.IsLoaded && zone.IsKeyboardFocused && zoneBounds.Top >= 0 && zoneBounds.Bottom <= pageScroll.ViewportHeight + 1,
                "Timezone dropdown is loaded, visible in the settings viewport and keyboard-focused before opening");
            var selectedZone = zone.SelectedItem;
            zone.IsDropDownOpen = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var popup = (Popup)zone.Template.FindName("PART_Popup", zone);
            var selectedItem = (ComboBoxItem)zone.ItemContainerGenerator.ContainerFromIndex(zone.SelectedIndex);
            Check(popup.IsOpen && selectedZone is TimeZoneInfo && ReferenceEquals(zone.SelectedItem, selectedZone)
                && selectedItem is { IsSelected: true, IsVisible: true, ActualHeight: > 0 },
                "Themed dropdown opens and retains the selected timezone");
            zone.IsDropDownOpen = false;
            settings.ShowPage("Historique"); await Task.Delay(100);
            Check(Tree(settings).OfType<TextBlock>().Any(t => t.Text.StartsWith("Aucun rappel envoyé")), "Empty history explains local reminder tracking");
            window.OpenPage("Comptes"); await Task.Delay(50); window.UpdateLayout();

            var avatarButton = Tree(window).OfType<Button>().First(b => b.Tag is Guid && b.ToolTip?.ToString() == "Changer le nom ou l’avatar");
            avatarButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100);
            var avatarEditor = window.OwnedWindows.OfType<AccountAppearanceWindow>().Single();
            Check(avatarEditor.IsVisible && Field<TextBox>(avatarEditor, "_name").IsVisible, "Clicking a dashboard avatar opens its personalization window"); avatarEditor.Close();

            var transparent = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 8 * 4], 8 * 4); transparent.Freeze();
            var transparentFile = AvatarStore.Save(directory, transparent);
            var originalTheme = window.Preferences.Current.ThemeMode;
            var originalContext = avatarButton.DataContext;
            preferences.Update(p => p with { Appearances = new(p.Appearances) { [id] = new("Studio", transparentFile) } });
            avatarButton.DataContext = vm;
            var transparentEditor = new AccountAppearanceWindow(window, preferences, id, theme); transparentEditor.Show();
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                window.Preferences.Update(p => p with { ThemeMode = mode }); await Task.Delay(100); vm.Tick(); window.UpdateLayout(); transparentEditor.UpdateLayout();
                var expected = ThemeManager.GetColor("AvatarBrush");
                Check(CenterColor(avatarButton) == expected && CenterColor(Field<Border>(transparentEditor, "_preview")) == expected
                    && !Tree(avatarButton).OfType<TextBlock>().Any(t => t.IsVisible),
                    $"Transparent imported avatar uses the {mode} theme background without initials in dashboard and preview");
            }
            transparentEditor.Close(); avatarButton.DataContext = originalContext;
            preferences.Update(p => p with { Appearances = new(p.Appearances) { [id] = appearance } });
            window.Preferences.Update(p => p with { ThemeMode = originalTheme }); AvatarStore.Remove(directory, transparentFile);

            var observed = service.State.ActiveAccount!.Snapshot!.FetchedAt;
            var status = new DashboardViewModel(true, preferences); status.Update(service.State);
            Check(status.StatusText.Contains(observed.ToLocalTime().ToString("HH:mm:ss")) && status.StatusHint.Contains("UTC"), "Footer shows the actual observation time and timezone");
            status.Update(service.State with { Accounts = service.State.Accounts.Select(a => a.IsActiveInCodex ? a with { Error = "offline" } : a).ToArray() });
            Check(status.StatusText.Contains(observed.ToLocalTime().ToString("HH:mm:ss")) && status.StatusText.Contains("échec de lecture des quotas"), "Failed quota read is distinct from an application update and keeps the last observation time");

            var opened = new List<Uri>();
            var calendar = new CalendarWindow(window, service, preferences, theme, openBrowser: opened.Add); calendar.Show();
            Check(Field<Button>(calendar, "_google").IsEnabled, "Google import is available without a prior manual export");
            Field<Button>(calendar, "_google").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var prepared = Field<TextBox>(calendar, "_path").Text;
            Check(File.Exists(prepared) && prepared.StartsWith(directory) && File.ReadAllText(prepared).Contains("Studio") && opened.Single().AbsolutePath.EndsWith("/settings/export") && Field<Border>(calendar, "_ready").IsVisible,
                "Google button prepares a private ICS file and displays the remaining import steps");
            Field<Button>(calendar, "_google").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Field<TextBox>(calendar, "_path").Text == prepared && Directory.GetFiles(Path.GetDirectoryName(prepared)!).Length == 1, "Repeated import clicks reuse the same prepared file");
            Tree(calendar).OfType<Expander>().Single().IsExpanded = true; calendar.UpdateLayout();
            Tree(calendar).OfType<Button>().First(b => b.Tag is CalendarEntry).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(opened.Last().AbsolutePath.EndsWith("/eventedit") && opened.Last().Query.Contains("action=TEMPLATE") && Field<TextBlock>(calendar, "_status").Text.Contains("Enregistrer"), "Single event button opens a Google draft without claiming it was saved");
            var countBefore = opened.Count; calendar.OpenEntry(new("old", "Old", "", DateTimeOffset.UtcNow.AddDays(-1)));
            Check(opened.Count == countBefore && Field<TextBlock>(calendar, "_status").Text.Contains("changé"), "Outdated events cannot open a stale Google draft");
            var icsPath = Path.Combine(directory, "calendar.ics"); calendar.ExportToFile(icsPath);
            Check(File.ReadAllText(icsPath).Contains("BEGIN:VEVENT") && File.ReadAllText(icsPath).Contains("Studio"), "Manual calendar export still includes account display names"); calendar.Close();
            var failedBrowser = new CalendarWindow(window, service, preferences, theme, openBrowser: _ => throw new IOException()); failedBrowser.Show();
            Field<Button>(failedBrowser, "_google").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(File.Exists(Field<TextBox>(failedBrowser, "_path").Text) && Field<TextBlock>(failedBrowser, "_status").Text.Contains("n’a pas pu"), "A browser failure preserves the prepared file and reports the failure"); failedBrowser.Close();

            var missing = Path.Combine(directory, "avatars", appearance.AvatarFile!); AvatarStore.Remove(directory, appearance.AvatarFile);
            Check(!File.Exists(missing) && AvatarStore.Load(directory, appearance.AvatarFile) is null, "Removing an avatar restores the initials fallback");
            var invalid = Path.Combine(directory, "bad.png"); File.WriteAllText(invalid, "not an image");
            bool rejected = false; try { AvatarStore.ReadImage(invalid); } catch { rejected = true; }
            Check(rejected, "Invalid avatar files are rejected");
            Console.WriteLine($"PASS {_checks} personalization, refresh and calendar WPF checks");
        }
        finally
        {
            foreach (Window child in window.OwnedWindows.Cast<Window>().ToArray()) child.Close();
            if (!Path.GetFullPath(directory).StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
            Directory.Delete(directory, true);
        }
    }
}
