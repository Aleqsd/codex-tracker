using System.Diagnostics;
using CodexTracker.App.Updates;
using AppThemeMode = CodexTracker.App.ThemeMode;

namespace CodexTracker.App;

internal sealed record ThemeChoice(ThemeMode Value, string Label) { public override string ToString() => Label; }
internal sealed record NumberChoice(int Value, string Label) { public override string ToString() => Label; }
internal sealed class SettingsWindow : ThemedWindow
{
    private readonly PreferencesStore _preferences;
    private readonly UpdateService _updates;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<(CheckBox Box, Func<TrackerPreferences, bool> Read)> _toggles = new();
    private readonly ComboBox _themeSelector;
    private readonly ComboBox _refreshSelector, _expirySelector;
    private readonly TextBlock _updateStatus;
    private readonly Button _check, _install;
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _nextCheckAt;
    private bool _updateBusy;
    private UpdateRelease? _release;
    private bool _syncing, _closed;
    private readonly bool _demo;
    private readonly StackPanel _navigation = new() { Margin = new Thickness(12, 18, 12, 0) };
    private readonly ScrollViewer _pageScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Dictionary<string, (StackPanel Content, Button Navigation)> _pages = new();
    private StackPanel _page = new();
    internal string CurrentPage { get; private set; } = "Général";

    public SettingsWindow(Window owner, PreferencesStore preferences, UpdateService updates, ThemeManager theme, bool demo)
        : base(owner, "Réglages", theme, 750, 620)
    {
        _preferences = preferences; _updates = updates; _demo = demo;
        MinWidth = 640;
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(154) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new Border { Child = _navigation, BorderThickness = new Thickness(0, 0, 1, 0) };
        sidebar.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); layout.Children.Add(sidebar);
        Grid.SetColumn(_pageScroll, 1); layout.Children.Add(_pageScroll);
        ContentScroll.Content = layout; ContentScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ContentScroll.VerticalContentAlignment = VerticalAlignment.Stretch; ContentScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Page("Général", "Adaptez le suivi à votre façon de travailler.");
        Section("Apparence");
        var themeRow = new Grid(); themeRow.ColumnDefinitions.Add(new ColumnDefinition()); themeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        var label = Ui.Text("Thème"); label.VerticalAlignment = VerticalAlignment.Center; themeRow.Children.Add(label);
        _themeSelector = new ComboBox { ItemsSource = new[] { new ThemeChoice(AppThemeMode.System, "Comme Windows"), new ThemeChoice(AppThemeMode.Light, "Clair"), new ThemeChoice(AppThemeMode.Dark, "Sombre") }, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        Grid.SetColumn(_themeSelector, 1); themeRow.Children.Add(_themeSelector); _page.Children.Add(themeRow);
        _themeSelector.SelectionChanged += (_, _) => { if (!_syncing && _themeSelector.SelectedValue is ThemeMode mode) Save(p => p with { ThemeMode = mode }); };
        Toggle("Aperçu au survol de l’icône", "Le quota et le prochain reset, sans ouvrir le panneau.", p => p.HoverPreview, (p, value) => p with { HoverPreview = value });
        Section("Actualisation", true);
        _refreshSelector = Choice("Compte actif", [new(1, "Chaque minute"), new(2, "Toutes les 2 min"), new(5, "Toutes les 5 min")], v => Save(p => p with { RefreshMinutes = v }));
        Toggle("Adapter à mon activité", "Passe à 10 min après 5 min sans clavier ni souris. Reprend la fréquence choisie à votre retour. La détection des comptes reste immédiate.", p => p.AdaptiveRefresh, (p, v) => p with { AdaptiveRefresh = v });
        Page("Notifications", "Choisissez les alertes utiles, au bon moment.");
        Section("Quotas");
        _page.Children.Add(Ui.Text("Prévenir quand le quota restant franchit un seuil.", 11, "MutedBrush"));
        var thresholds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 2) };
        thresholds.Children.Add(PreferenceCheck("20 %", p => p.Alert20, (p, v) => p with { Alert20 = v }));
        thresholds.Children.Add(PreferenceCheck("10 %", p => p.Alert10, (p, v) => p with { Alert10 = v }));
        thresholds.Children.Add(PreferenceCheck("5 %", p => p.Alert5, (p, v) => p with { Alert5 = v })); _page.Children.Add(thresholds);
        var test = new Button { Content = "Tester une notification", Style = (Style)FindResource("QuietButton"), Padding = new Thickness(0, 7, 0, 1), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11 };
        test.Click += (_, _) => ((App)System.Windows.Application.Current).ShowTestNotification(); _page.Children.Add(test);
        Toggle("Prévenir après un reset", null, p => p.ResetNotifications, (p, value) => p with { ResetNotifications = value });
        Section("Resets en réserve", true);
        Toggle("Prévenir avant l’expiration des réserves", "Un rappel par reset, d’après le dernier relevé disponible.", p => p.ExpiryNotifications, (p, v) => p with { ExpiryNotifications = v });
        _expirySelector = Choice("Prévenir à l’avance", [new(24, "24 heures"), new(72, "3 jours"), new(168, "7 jours")], v => Save(p => p with { ExpiryLeadHours = v }));
        Page("Calendrier", "Retrouvez les échéances de vos comptes dans votre agenda.");
        var calendar = new Button { Content = "Exporter les échéances…", HorizontalAlignment = HorizontalAlignment.Left };
        calendar.Click += (_, _) => ((MainWindow)owner).OpenCalendar(); _page.Children.Add(calendar);
        var calendarHint = Ui.Text("Fichier .ics compatible Google Calendar, Outlook et Apple Calendar.", 11, "MutedBrush"); calendarHint.Margin = new Thickness(0, 7, 0, 0); _page.Children.Add(calendarHint);
        Page("Application", "Démarrage, mises à jour et version installée.");
        Section("Démarrage");
        var startup = new CheckBox { Content = "Démarrer avec Windows", IsChecked = StartupSettings.IsEnabled, IsEnabled = !demo, Margin = new Thickness(0, 3, 0, 4) };
        startup.Click += (_, _) => { try { StartupSettings.SetEnabled(startup.IsChecked == true); } catch (Exception error) { ShowError(error.Message); startup.IsChecked = StartupSettings.IsEnabled; } }; _page.Children.Add(startup);
        Section("Mises à jour", true);
        _page.Children.Add(Ui.Text($"Version {_updates.CurrentVersion}", 12));
        var lastUpdate = demo ? null : updates.ReadLastResult();
        _updateStatus = Ui.Text(demo ? "Les mises à jour sont désactivées dans la démonstration." : lastUpdate is not null ? Display.SafeText(lastUpdate.Message, preferences.Current.PrivacyMode) : "Vérifiez les versions publiées sur le dépôt officiel.", 11, "MutedBrush"); _updateStatus.Margin = new Thickness(0, 7, 0, 12); _page.Children.Add(_updateStatus);
        var actions = new WrapPanel();
        _check = new Button { Content = "Rechercher une mise à jour", IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }; _check.Click += async (_, _) => await CheckAsync(); actions.Children.Add(_check);
        _install = new Button { Content = "Installer et relancer", Visibility = Visibility.Collapsed, Style = (Style)FindResource("PrimaryButton") }; _install.Click += async (_, _) => await InstallAsync(); actions.Children.Add(_install); _page.Children.Add(actions);
        var releases = new Button { Content = "Voir les versions sur GitHub ↗", Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 8, 0, 1), FontSize = 11 };
        releases.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(UpdateService.ReleasesPage.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) { ShowError(error.Message); }
        };
        _page.Children.Add(releases);
        if (!demo && updates.ReadCachedCheck() is { } cached)
        {
            ShowCheckResult(cached);
            if (lastUpdate is { Success: false }) _updateStatus.Text = Display.SafeText(lastUpdate.Message, preferences.Current.PrivacyMode) + "\n" + _updateStatus.Text;
        }
        _updateTimer.Tick += (_, _) => SyncUpdateButton();
        _updateTimer.Start();
        var quit = new Button { Content = "Quitter Codex Tracker", Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-10, 19, 0, 0) };
        quit.Click += async (_, _) => await ((App)System.Windows.Application.Current).ExitAsync(); _page.Children.Add(quit);
        preferences.Changed += PreferencesChanged;
        Closed += (_, _) => { _closed = true; _updateTimer.Stop(); _lifetime.Cancel(); preferences.Changed -= PreferencesChanged; };
        Sync();
        ShowPage("Général");
    }
    private void Page(string title, string description)
    {
        _page = new StackPanel { Margin = new Thickness(26, 24, 24, 24) };
        var heading = Ui.Text(title, 21); heading.FontWeight = FontWeights.SemiBold; _page.Children.Add(heading);
        var hint = Ui.Text(description, 12, "MutedBrush"); hint.Margin = new Thickness(0, 7, 0, 26); _page.Children.Add(hint);
        var button = new Button { Content = title, Style = (Style)FindResource("SettingsNavigation"), Margin = new Thickness(0, 0, 0, 4), Tag = title };
        button.Click += (_, _) => ShowPage(title); _navigation.Children.Add(button); _pages.Add(title, (_page, button));
    }
    internal void ShowPage(string title)
    {
        if (!_pages.TryGetValue(title, out var page)) return;
        CurrentPage = title; _pageScroll.Content = page.Content; _pageScroll.ScrollToTop();
        foreach (var (name, value) in _pages)
        {
            value.Navigation.SetResourceReference(BackgroundProperty, name == title ? "ButtonBrush" : "BackgroundBrush");
            value.Navigation.FontWeight = name == title ? FontWeights.SemiBold : FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetItemStatus(value.Navigation, name == title ? "Section active" : "");
        }
    }
    private void Section(string title, bool separator = false)
    {
        if (separator) { var line = new Border { Height = 1, Margin = new Thickness(0, 19, 0, 17) }; line.SetResourceReference(Border.BackgroundProperty, "LineBrush"); _page.Children.Add(line); }
        var text = Ui.Text(title, 13); text.FontWeight = FontWeights.SemiBold; text.Margin = new Thickness(0, 0, 0, 12); _page.Children.Add(text);
    }
    private ComboBox Choice(string title, NumberChoice[] choices, Action<int> save)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        var label = Ui.Text(title); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        combo.SelectionChanged += (_, _) => { if (!_syncing && combo.SelectedValue is int value) save(value); };
        Grid.SetColumn(combo, 1); row.Children.Add(combo); _page.Children.Add(row); return combo;
    }
    private CheckBox PreferenceCheck(string title, Func<TrackerPreferences, bool> read, Func<TrackerPreferences, bool, TrackerPreferences> set)
    {
        var check = new CheckBox { Content = title, Margin = new Thickness(0, 0, 24, 0) };
        check.Click += (_, _) => { if (!_syncing) Save(p => set(p, check.IsChecked == true)); }; _toggles.Add((check, read)); return check;
    }
    private void Toggle(string title, string? description, Func<TrackerPreferences, bool> read, Func<TrackerPreferences, bool, TrackerPreferences> set)
    {
        var check = PreferenceCheck(title, read, set); check.Margin = new Thickness(0, 15, 0, 0); _page.Children.Add(check);
        if (description is not null) { var hint = Ui.Text(description, 11, "MutedBrush"); hint.Margin = new Thickness(28, 5, 0, 0); _page.Children.Add(hint); }
    }
    private void Save(Func<TrackerPreferences, TrackerPreferences> update)
    {
        try { _preferences.Update(update); }
        catch (Exception error) { ShowError(error.Message); Sync(); }
    }
    private void PreferencesChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Sync);
    private void Sync()
    {
        if (_closed) return;
        _syncing = true; _themeSelector.SelectedValue = _preferences.Current.ThemeMode;
        _refreshSelector.SelectedValue = _preferences.Current.RefreshMinutes;
        _expirySelector.SelectedValue = _preferences.Current.ExpiryLeadHours;
        _expirySelector.IsEnabled = _preferences.Current.ExpiryNotifications;
        foreach (var (box, read) in _toggles) box.IsChecked = read(_preferences.Current); _syncing = false;
        _updateStatus.Text = Display.SafeText(_updateStatus.Text, _preferences.Current.PrivacyMode);
    }
    private void ShowError(string message) => new TrackerDialog(this, "Action indisponible", Display.SafeText(message, _preferences.Current.PrivacyMode), "Fermer", null).ShowDialog();
    private async Task CheckAsync()
    {
        if (_demo) return;
        _updateBusy = true; SyncUpdateButton(); _install.Visibility = Visibility.Collapsed; _updateStatus.Text = "Recherche en cours…";
        try
        {
            var result = await _updates.CheckDetailedAsync(_lifetime.Token);
            if (_closed) return;
            ShowCheckResult(result);
        }
        catch (OperationCanceledException) { if (!_closed) _updateStatus.Text = "Recherche annulée."; }
        catch (Exception error) { if (!_closed) _updateStatus.Text = Display.SafeText(error.Message, _preferences.Current.PrivacyMode); }
        finally { _updateBusy = false; if (!_closed) SyncUpdateButton(); }
    }
    private void ShowCheckResult(UpdateCheckResult result)
    {
        _release = result.Release; _nextCheckAt = result.NextCheckAt;
        var text = result.IsVerifiedNow
            ? _release is null ? "Vous utilisez la dernière version publiée." : $"Version {_release.Version} disponible."
            : result.Message + (_release is null ? " La version actuelle n’a pas été revérifiée." : $" Version {_release.Version} connue dans le cache.");
        if (result.VerifiedAt is { } checkedAt)
            text += $"\n{(result.IsVerifiedNow ? "Vérifié" : "Cache vérifié")} le {checkedAt.ToLocalTime():dd/MM/yyyy à HH:mm:ss}.";
        if (result.NextCheckAt is { } next && next > DateTimeOffset.UtcNow)
            text += $"\nNouvelle vérification possible dès le {next.ToLocalTime():dd/MM/yyyy à HH:mm:ss}.";
        _updateStatus.Text = text;
        _install.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
        SyncUpdateButton();
    }
    private void SyncUpdateButton()
    {
        _check.IsEnabled = !_demo && !_updateBusy && !(_nextCheckAt > DateTimeOffset.UtcNow);
        _check.ToolTip = _nextCheckAt > DateTimeOffset.UtcNow ? $"Disponible à {_nextCheckAt.Value.ToLocalTime():HH:mm:ss}." : null;
    }
    private async Task InstallAsync()
    {
        if (_release is null || _demo) return;
        _updateBusy = true; SyncUpdateButton(); _install.IsEnabled = false; _updateStatus.Text = "Téléchargement et vérification de la mise à jour…";
        try
        {
            await _updates.StageAndLaunchAsync(_release, Environment.ProcessId, _lifetime.Token);
            if (!_closed) await ((App)System.Windows.Application.Current).ExitAsync();
        }
        catch (OperationCanceledException) { if (!_closed) _updateStatus.Text = "Installation annulée."; }
        catch (Exception error) { if (!_closed) _updateStatus.Text = Display.SafeText(error.Message, _preferences.Current.PrivacyMode); }
        finally { _updateBusy = false; if (!_closed) { SyncUpdateButton(); _install.IsEnabled = true; } }
    }
}
