using System.Diagnostics;
using CodexTracker.App.Updates;
using AppThemeMode = CodexTracker.App.ThemeMode;

namespace CodexTracker.App;

internal sealed record ThemeChoice(ThemeMode Value, string Label) { public override string ToString() => Label; }
internal sealed class SettingsWindow : ThemedWindow
{
    private readonly PreferencesStore _preferences;
    private readonly UpdateService _updates;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<(CheckBox Box, Func<TrackerPreferences, bool> Read)> _toggles = new();
    private readonly ComboBox _themeSelector;
    private readonly TextBlock _updateStatus;
    private readonly Button _check, _install;
    private UpdateRelease? _release;
    private bool _syncing, _closed;
    private readonly bool _demo;

    public SettingsWindow(Window owner, PreferencesStore preferences, UpdateService updates, ThemeManager theme, bool demo)
        : base(owner, "Réglages", theme, 540, 720)
    {
        _preferences = preferences; _updates = updates; _demo = demo;
        Section("Apparence");
        var themeRow = new Grid(); themeRow.ColumnDefinitions.Add(new ColumnDefinition()); themeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
        var label = Ui.Text("Thème"); label.VerticalAlignment = VerticalAlignment.Center; themeRow.Children.Add(label);
        _themeSelector = new ComboBox { ItemsSource = new[] { new ThemeChoice(AppThemeMode.System, "Comme Windows"), new ThemeChoice(AppThemeMode.Light, "Clair"), new ThemeChoice(AppThemeMode.Dark, "Sombre") }, DisplayMemberPath = "Label", SelectedValuePath = "Value" };
        Grid.SetColumn(_themeSelector, 1); themeRow.Children.Add(_themeSelector); Body.Children.Add(themeRow);
        _themeSelector.SelectionChanged += (_, _) => { if (!_syncing && _themeSelector.SelectedValue is ThemeMode mode) Save(p => p with { ThemeMode = mode }); };
        Toggle("Masquer les identités", "Remplace les adresses par Compte 01, Compte 02…", p => p.PrivacyMode, (p, value) => p with { PrivacyMode = value });
        Toggle("Aperçu au survol de l’icône", "Le quota et le prochain reset, sans ouvrir le panneau.", p => p.HoverPreview, (p, value) => p with { HoverPreview = value });
        Section("Notifications", true);
        Body.Children.Add(Ui.Text("Prévenir quand le quota restant franchit un seuil.", 11, "MutedBrush"));
        var thresholds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 2) };
        thresholds.Children.Add(PreferenceCheck("20 %", p => p.Alert20, (p, v) => p with { Alert20 = v }));
        thresholds.Children.Add(PreferenceCheck("10 %", p => p.Alert10, (p, v) => p with { Alert10 = v }));
        thresholds.Children.Add(PreferenceCheck("5 %", p => p.Alert5, (p, v) => p with { Alert5 = v })); Body.Children.Add(thresholds);
        var test = new Button { Content = "Tester une notification", Style = (Style)FindResource("QuietButton"), Padding = new Thickness(0, 7, 0, 1), HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11 };
        test.Click += (_, _) => ((App)System.Windows.Application.Current).ShowTestNotification(); Body.Children.Add(test);
        Toggle("Prévenir après un reset", null, p => p.ResetNotifications, (p, value) => p with { ResetNotifications = value });
        Section("Démarrage", true);
        var startup = new CheckBox { Content = "Démarrer avec Windows", IsChecked = StartupSettings.IsEnabled, IsEnabled = !demo, Margin = new Thickness(0, 3, 0, 4) };
        startup.Click += (_, _) => { try { StartupSettings.SetEnabled(startup.IsChecked == true); } catch (Exception error) { ShowError(error.Message); startup.IsChecked = StartupSettings.IsEnabled; } }; Body.Children.Add(startup);
        Section("Mises à jour", true);
        Body.Children.Add(Ui.Text($"Version {_updates.CurrentVersion}", 12));
        var lastUpdate = demo ? null : updates.ReadLastResult();
        _updateStatus = Ui.Text(demo ? "Les mises à jour sont désactivées dans la démonstration." : lastUpdate is not null ? Display.SafeText(lastUpdate.Message, preferences.Current.PrivacyMode) : "Vérifiez les versions publiées sur le dépôt officiel.", 11, "MutedBrush"); _updateStatus.Margin = new Thickness(0, 7, 0, 12); Body.Children.Add(_updateStatus);
        var actions = new WrapPanel();
        _check = new Button { Content = "Rechercher une mise à jour", IsEnabled = !demo, Margin = new Thickness(0, 0, 8, 0) }; _check.Click += async (_, _) => await CheckAsync(); actions.Children.Add(_check);
        _install = new Button { Content = "Installer et relancer", Visibility = Visibility.Collapsed, Style = (Style)FindResource("PrimaryButton") }; _install.Click += async (_, _) => await InstallAsync(); actions.Children.Add(_install); Body.Children.Add(actions);
        var quit = new Button { Content = "Quitter Codex Tracker", Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-10, 19, 0, 0) };
        quit.Click += async (_, _) => await ((App)System.Windows.Application.Current).ExitAsync(); Body.Children.Add(quit);
        preferences.Changed += PreferencesChanged;
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); preferences.Changed -= PreferencesChanged; };
        Sync();
    }
    private void Section(string title, bool separator = false)
    {
        if (separator) { var line = new Border { Height = 1, Margin = new Thickness(0, 19, 0, 17) }; line.SetResourceReference(Border.BackgroundProperty, "LineBrush"); Body.Children.Add(line); }
        var text = Ui.Text(title, 13); text.FontWeight = FontWeights.SemiBold; text.Margin = new Thickness(0, 0, 0, 12); Body.Children.Add(text);
    }
    private CheckBox PreferenceCheck(string title, Func<TrackerPreferences, bool> read, Func<TrackerPreferences, bool, TrackerPreferences> set)
    {
        var check = new CheckBox { Content = title, Margin = new Thickness(0, 0, 24, 0) };
        check.Click += (_, _) => { if (!_syncing) Save(p => set(p, check.IsChecked == true)); }; _toggles.Add((check, read)); return check;
    }
    private void Toggle(string title, string? description, Func<TrackerPreferences, bool> read, Func<TrackerPreferences, bool, TrackerPreferences> set)
    {
        var check = PreferenceCheck(title, read, set); check.Margin = new Thickness(0, 15, 0, 0); Body.Children.Add(check);
        if (description is not null) { var hint = Ui.Text(description, 11, "MutedBrush"); hint.Margin = new Thickness(28, 5, 0, 0); Body.Children.Add(hint); }
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
        foreach (var (box, read) in _toggles) box.IsChecked = read(_preferences.Current); _syncing = false;
        _updateStatus.Text = Display.SafeText(_updateStatus.Text, _preferences.Current.PrivacyMode);
    }
    private void ShowError(string message) => new TrackerDialog(this, "Action indisponible", Display.SafeText(message, _preferences.Current.PrivacyMode), "Fermer", null).ShowDialog();
    private async Task CheckAsync()
    {
        if (_demo) return;
        _check.IsEnabled = false; _install.Visibility = Visibility.Collapsed; _updateStatus.Text = "Recherche en cours…";
        try
        {
            _release = await _updates.CheckAsync(_lifetime.Token);
            if (_closed) return;
            _updateStatus.Text = _release is null ? "Vous utilisez la dernière version publiée." : $"Version {_release.Version} disponible. Le tracker sera relancé après l’installation.";
            _install.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException) { if (!_closed) _updateStatus.Text = "Recherche annulée."; }
        catch (Exception error) { if (!_closed) _updateStatus.Text = Display.SafeText(error.Message, _preferences.Current.PrivacyMode); }
        finally { if (!_closed) _check.IsEnabled = true; }
    }
    private async Task InstallAsync()
    {
        if (_release is null || _demo) return;
        _check.IsEnabled = false; _install.IsEnabled = false; _updateStatus.Text = "Téléchargement et vérification de la mise à jour…";
        try
        {
            await _updates.StageAndLaunchAsync(_release, Environment.ProcessId, _lifetime.Token);
            if (!_closed) await ((App)System.Windows.Application.Current).ExitAsync();
        }
        catch (OperationCanceledException) { if (!_closed) _updateStatus.Text = "Installation annulée."; }
        catch (Exception error) { if (!_closed) _updateStatus.Text = Display.SafeText(error.Message, _preferences.Current.PrivacyMode); }
        finally { if (!_closed) { _check.IsEnabled = true; _install.IsEnabled = true; } }
    }
}
