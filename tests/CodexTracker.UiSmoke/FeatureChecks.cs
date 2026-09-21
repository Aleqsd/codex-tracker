using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Core;

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
            var settings = new SettingsWindow(window, preferences, new UpdateService(), theme, true); settings.Show();
            Check(settings.CurrentPage == "Général" && Tree(settings).OfType<Button>().Any(b => b.Content?.ToString() == "Notifications"), "Settings open on General with section navigation");
            Field<ComboBox>(settings, "_refreshSelector").SelectedValue = 1;
            var adaptive = Tree(settings).OfType<CheckBox>().Single(c => c.Content?.ToString() == "Adapter à mon activité");
            adaptive.IsChecked = true; adaptive.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Field<ComboBox>(settings, "_expirySelector").SelectedValue = 72;
            await Task.Delay(100);
            Check(preferences.Current.RefreshMinutes == 1 && preferences.Current.AdaptiveRefresh && preferences.Current.ExpiryLeadHours == 72,
                "Refresh, adaptive mode and expiry horizon save from actual controls");
            var labels = Tree(settings).OfType<TextBlock>().Select(t => t.Text).ToArray();
            Check(labels.Contains("Chaque minute") && !labels.Any(t => t.Contains("NumberChoice")), "Refresh selector displays a readable label");
            Tree(settings).OfType<Button>().Single(b => b.Content?.ToString() == "Notifications").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100);
            Check(settings.CurrentPage == "Notifications" && Tree(settings).OfType<TextBlock>().Any(t => t.Text == "3 jours"), "Settings navigation shows the selected section and preserves values");
            var expiry = Field<ComboBox>(settings, "_expirySelector"); expiry.IsDropDownOpen = true; await Task.Delay(100);
            var popup = (Popup)expiry.Template.FindName("PART_Popup", expiry);
            var selectedItem = (ComboBoxItem)expiry.ItemContainerGenerator.ContainerFromIndex(expiry.SelectedIndex);
            Check(popup.IsOpen && selectedItem.Template.FindName("SelectedMark", selectedItem) is FrameworkElement mark && mark.IsVisible, "Dropdown popup opens with a visible selection checkmark");
            expiry.IsDropDownOpen = false; settings.Close();

            var avatarButton = Tree(window).OfType<Button>().First(b => b.Tag is Guid && b.ToolTip?.ToString() == "Changer le nom ou l’avatar");
            avatarButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); await Task.Delay(100);
            var avatarEditor = window.OwnedWindows.OfType<AccountAppearanceWindow>().Single();
            Check(avatarEditor.IsVisible && Field<TextBox>(avatarEditor, "_name").IsVisible, "Clicking a dashboard avatar opens its personalization window"); avatarEditor.Close();

            var observed = service.State.ActiveAccount!.Snapshot!.FetchedAt;
            var status = new DashboardViewModel(true, preferences); status.Update(service.State);
            Check(status.StatusText.Contains(observed.ToLocalTime().ToString("HH:mm:ss")) && status.StatusHint.Contains("UTC"), "Footer shows the actual observation time and timezone");
            status.Update(service.State with { Accounts = service.State.Accounts.Select(a => a.IsActiveInCodex ? a with { Error = "offline" } : a).ToArray() });
            Check(status.StatusText.Contains(observed.ToLocalTime().ToString("HH:mm:ss")) && status.StatusText.Contains("échec"), "Failed refresh keeps the last successful observation time");

            var calendar = new CalendarWindow(window, service, preferences, theme); calendar.Show();
            Check(!Field<Button>(calendar, "_google").IsEnabled, "Calendar guides export before opening Google import");
            var icsPath = Path.Combine(directory, "calendar.ics"); calendar.ExportToFile(icsPath);
            var ics = File.ReadAllText(icsPath);
            Check(ics.Contains("BEGIN:VEVENT") && ics.Contains("Studio") && Field<Button>(calendar, "_google").IsEnabled,
                "Calendar writes account display names to ICS and enables the Google import action"); calendar.Close();

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
