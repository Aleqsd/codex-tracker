using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using CodexTracker.App.Updates;

namespace CodexTracker.App;

public partial class MainWindow : Window
{
    private readonly ITrackerService _service;
    private readonly DashboardViewModel _model;
    private readonly PreferencesStore _preferences;
    private readonly UpdateService _updates;
    private readonly AutomaticUpdater? _automaticUpdates;
    private readonly bool _demo;
    private bool _installingUpdate;
    private readonly ResetsView _resets;
    private SettingsView? _settings;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _canClose, _refreshing;
    internal ThemeManager Theme { get; }
    internal PreferencesStore Preferences => _preferences;
    internal ITrackerService TrackerService => _service;
    internal ReminderRuntime Reminders { get; }
    internal ApplicationCommands Commands { get; }
    internal Mcp.TrackerControl? Assistant { get; set; }
    internal string? AssistantError { get; set; }
    internal SettingsView Settings
    {
        get
        {
            if (_settings is null)
            {
                _settings = new(this, _preferences, _updates, _demo);
                SettingsTab.Content = _settings;
            }
            return _settings;
        }
    }

    internal MainWindow(ITrackerService service, bool demo, PreferencesStore preferences, UpdateService updates)
    {
        _service = service; _preferences = preferences; _updates = updates; _demo = demo;
        _updates.SetIncludePrereleases(preferences.Current.IncludePrereleaseUpdates);
        if (!demo && UpdateService.CanSelfUpdate) _automaticUpdates = new(updates);
        Reminders = new(service, preferences, demo);
        Commands = new(preferences, Reminders.Secrets);
        Theme = new ThemeManager(preferences);
        _model = new DashboardViewModel(demo, preferences);
        InitializeComponent();
        _updates.PreparationChanged += UpdatePreparationChanged;
        UpdatePresentation();
        _resets = new ResetsView(_preferences, id => new CalendarWindow(this, _service, _preferences, Theme, id).ShowDialog(), () => _ = RefreshAsync()); ResetsTab.Content = _resets;
        MainTabs.SelectionChanged += (_, e) =>
        { if (ReferenceEquals(e.OriginalSource, MainTabs) && SettingsTab.IsSelected) _ = Settings; };
        if (demo) Title = "Codex Tracker (démo)";
        DataContext = _model;
        UpdateModel();
        _service.Changed += Service_Changed;
        _preferences.Changed += Preferences_Changed;
        Theme.Changed += Theme_Changed;
        _clockTimer.Tick += (_, _) => { _model.Tick(); _resets.Tick(); RefreshHealth(); };
        Closing += OnClosing;
        SourceInitialized += (_, _) => { Ui.ConstrainInitialSize(this); ApplyChrome(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HideToTray(); if (e.Key == Key.F5) _ = RefreshAsync(); };
    }
    public async Task InitializeAsync()
    {
        await _service.InitializeAsync(_lifetime.Token);
        UpdateModel(); _clockTimer.Start();
        if (_automaticUpdates is not null)
        {
            _automaticUpdates.SetEnabled(_preferences.Current.DownloadUpdatesAutomatically);
            _ = _automaticUpdates.RunAsync();
        }
    }
    private void UpdateModel()
    {
        if (_canClose) return;
        _model.Update(_service.State); _resets.Update(_service.State);
        RefreshHealth();
    }
    private void RefreshHealth()
    {
        RecoveryBanner.Visibility = HealthWarnings().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _settings?.RefreshHealth();
    }
    private void Service_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdateModel);
    private void Preferences_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(async () =>
    {
        var changed = _updates.IncludePrereleases != _preferences.Current.IncludePrereleaseUpdates;
        _updates.SetIncludePrereleases(_preferences.Current.IncludePrereleaseUpdates);
        UpdateModel(); UpdatePresentation();
        _automaticUpdates?.SetEnabled(!_installingUpdate && _preferences.Current.DownloadUpdatesAutomatically);
        if (changed)
        {
            try { await _updates.LoadPreparedAsync(_lifetime.Token); }
            catch (OperationCanceledException) { }
        }
    });
    private void Theme_Changed(object? sender, EventArgs e) { ApplyChrome(); UpdateModel(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_canClose) { e.Cancel = true; HideToTray(); } }
    private void HideToTray() { Hide(); ShowInTaskbar = false; }
    public void ShowPanel() { ShowInTaskbar = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Ui.EnsureWindowVisible(this); Activate(); }
    internal void ShowResetWeek() { ShowResets(); _resets.ShowWeek(); }
    internal void ShowResets() { ShowPanel(); ResetsTab.IsSelected = true; }
    internal void OpenPage(string page)
    {
        if (page == "Resets") { ShowResets(); return; }
        if (page == "Comptes") { ShowPanel(); AccountsTab.IsSelected = true; return; }
        if (page is not ("Général" or "Rappels" or "Canaux" or "Historique" or "Calendrier" or "Assistants" or "Application")) throw new ArgumentException("Page inconnue.");
        ShowSettings(); Settings.ShowPage(page);
    }
    public void PrepareExit()
    {
        _canClose = true; _lifetime.Cancel(); _clockTimer.Stop();
        Assistant?.Dispose();
        foreach (Window child in OwnedWindows.Cast<Window>().ToArray()) child.Close();
        _service.Changed -= Service_Changed; _preferences.Changed -= Preferences_Changed; Theme.Changed -= Theme_Changed;
        _automaticUpdates?.Dispose(); _updates.PreparationChanged -= UpdatePreparationChanged;
        _settings?.Dispose();
        Reminders.Dispose(); Theme.Dispose(); _updates.Dispose();
    }
    private void ApplyChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = Theme.IsDark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, 4);
        int rounded = 2; DwmSetWindowAttribute(handle, 33, ref rounded, 4);
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Hide_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void Recovery_Click(object sender, RoutedEventArgs e) => OpenPage("Application");
    private void ExpectedReset_Click(object sender, RoutedEventArgs e)
    {
        var account = _model.Accounts.FirstOrDefault(a => a.HasResetEstimate);
        if (account is null) return;
        if (AccountsList.ItemContainerGenerator.ContainerFromItem(account) is FrameworkElement row) row.BringIntoView();
    }
    internal string[] HealthWarnings() => new[] { _preferences.RecoveryWarning, Reminders.Error, AssistantError }
        .Concat((_service as Codex.TrackerService)?.RecoveryWarnings ?? [])
        .Where(message => !string.IsNullOrWhiteSpace(message)).Select(message => message!).Distinct().ToArray();
    internal string BuildDiagnostic() => SupportDiagnostics.Create(_updates.CurrentVersion, _preferences.Current,
        (_service as Codex.TrackerService)?.GetCompatibilityDiagnostic(), Reminders.Error is null,
        Assistant is not null && AssistantError is null, _demo ? WindowsNotificationState.Unknown : WindowsNotificationDelivery.ReadState(),
        _updates.Prepared is not null, _demo);
    private void UpdatePreparationChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdatePresentation);
    private void UpdatePresentation()
    {
        if (_canClose) return;
        PresentUpdate(_updates.Prepared?.Release.Version, _updates.IsPreparing);
    }
    internal void PresentUpdate(string? version, bool downloading)
    {
        UpdateBanner.Visibility = version is not null || downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeadline.Text = downloading ? "Une mise à jour se télécharge…" : $"Version {version} prête à installer";
        UpdateHint.Text = downloading ? "Le suivi continue en arrière-plan." : _preferences.Current.InstallUpdatesAtStartup
            ? "Maintenant, ou automatiquement au prochain démarrage du tracker." : "Téléchargée et vérifiée. Vos comptes et réglages sont conservés.";
        if (!_installingUpdate && _updates.Prepared?.AutomaticAttempted == true) UpdateHint.Text = "Installation précédente interrompue. Vous pouvez réessayer.";
        UpdateNowButton.Visibility = version is not null && !downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowButton.IsEnabled = !_installingUpdate;
        UpdateNowButton.Content = _installingUpdate ? "Installation…" : "Mettre à jour et relancer";
    }
    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        try { await InstallReadyUpdateAsync(); }
        catch (Exception error) { if (!_canClose) ShowMessage("Mise à jour indisponible", error.Message); }
    }
    internal async Task InstallReadyUpdateAsync()
    {
        if (_demo || _installingUpdate || _canClose) return;
        _installingUpdate = true; _automaticUpdates?.SetEnabled(false); UpdatePresentation();
        try
        {
            if (!await _updates.LaunchPreparedAsync(false, false, _lifetime.Token))
                throw new IOException("La mise à jour n’est plus prête. Recherchez-la à nouveau dans les réglages.");
            await ((App)System.Windows.Application.Current).ExitAsync();
        }
        finally
        {
            _installingUpdate = false;
            if (!_canClose) { UpdatePresentation(); _automaticUpdates?.SetEnabled(_preferences.Current.DownloadUpdatesAutomatically); }
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    public async Task RefreshAsync()
    {
        if (_refreshing || _service.State.IsBusy) return;
        _refreshing = true;
        try { await _service.RefreshAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsVisible && !_canClose) ShowMessage("Actualisation indisponible", error.Message); }
        finally { _refreshing = false; }
    }
    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_canClose) ShowMessage("L’action n’a pas abouti", error.Message); }
    }
    private static Guid Id(object sender) => (Guid)((FrameworkElement)sender).Tag;
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.ImportCurrentAccountAsync(_lifetime.Token));
    private async void Onboarding_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.CompleteOnboardingAsync(_lifetime.Token));
    private void Details_Click(object sender, RoutedEventArgs e) => OpenHistory(Id(sender));
    private void Avatar_Click(object sender, RoutedEventArgs e) => OpenAppearance(Id(sender));
    internal AccountAppearanceWindow OpenAppearance(Guid id)
    {
        var existing = OwnedWindows.OfType<AccountAppearanceWindow>().FirstOrDefault(w => w.AccountId == id);
        if (existing is not null) { existing.Activate(); return existing; }
        var editor = new AccountAppearanceWindow(this, _preferences, id, Theme, _service.State.Accounts.FirstOrDefault(a => a.Profile.Id == id)?.Profile.Email);
        editor.Show(); return editor;
    }
    private void Advice_Click(object sender, RoutedEventArgs e)
    {
        if (_model.AdviceAccountId is Guid id) OpenHistory(id);
    }
    internal HistoryWindow OpenHistory(Guid id)
    {
        var existing = OwnedWindows.OfType<HistoryWindow>().FirstOrDefault(w => w.AccountId == id);
        if (existing is not null) { existing.Activate(); return existing; }
        var details = new HistoryWindow(this, _service, _preferences, id, Theme);
        details.Show(); return details;
    }
    internal void ShowMessage(string title, string message) => new TrackerDialog(this, title, Display.SafeText(message, _preferences.Current.PrivacyMode), "Fermer", null).ShowDialog();
    public void ShowSettings()
    {
        ShowPanel(); _ = Settings; SettingsTab.IsSelected = true;
    }
    internal void OpenCalendar() => new CalendarWindow(this, _service, _preferences, Theme).ShowDialog();
    public void SaveScreenshot(string path, double dpi) => Ui.SaveScreenshot(this, path, dpi);
    public void SaveDetailsScreenshot(string path, double dpi)
    {
        if (!_model.IsDemo) throw new InvalidOperationException("Les captures de détails nécessitent le mode démonstration.");
        var account = _service.State.Accounts.FirstOrDefault(); if (account is null) return;
        var window = OpenHistory(account.Profile.Id); window.UpdateLayout(); Ui.SaveScreenshot(window, path, dpi); window.Close();
    }
    public void SaveSettingsScreenshot(string path, double dpi)
    {
        if (!_model.IsDemo) throw new InvalidOperationException("Les captures des réglages nécessitent le mode démonstration.");
        ShowSettings(); UpdateLayout(); Ui.SaveScreenshot(this, path, dpi);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal static class StartupSettings
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexTracker";
    public static bool IsEnabled { get { using var key = Registry.CurrentUser.OpenSubKey(KeyPath); return key?.GetValue(ValueName) is string; } }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue(ValueName, false);
    }
}
