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
    private bool _installingUpdate, _checkingUpdate;
    // A footer check result stays readable for a few seconds, then the version returns.
    private readonly System.Windows.Threading.DispatcherTimer _versionNotice = new() { Interval = TimeSpan.FromSeconds(8) };
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
                _settings = new(this, _preferences, _updates, _demo, _automaticUpdates);
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
        _model = new DashboardViewModel(demo, preferences) { Forecast = service.GetForecast };
        InitializeComponent();
        ShowVersion();
        CheckUpdateButton.IsEnabled = !_demo;
        _versionNotice.Tick += (_, _) => { _versionNotice.Stop(); if (!_canClose) ShowVersion(); };
        _updates.PreparationChanged += UpdatePreparationChanged;
        UpdatePresentation();
        _resets = new ResetsView(_preferences, id => new CalendarWindow(this, _service, _preferences, Theme, id).ShowDialog(),
            () => _ = RefreshAsync(), () => OpenManualCodexReset(), (entry, enabled) =>
                Commands.SetResetAlert(_service.State, entry.Account.Profile.Id, entry.Kind, entry.At!.Value, enabled, PreviewClock.UtcNow, Commands.Revision));
        ResetsTab.Content = _resets;
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
    private void Preferences_Changed(object? sender, EventArgs e)
    {
        _service.SynchronizeGlobalResetMonitoring();
        Dispatcher.InvokeAsync(async () =>
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
    }
    private void Theme_Changed(object? sender, EventArgs e) { ApplyChrome(); UpdateModel(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_canClose) { e.Cancel = true; HideToTray(); } }
    private void HideToTray() { Hide(); ShowInTaskbar = false; }
    internal bool Offscreen { get; set; }
    public void ShowPanel()
    {
        if (Offscreen) { Show(); return; }
        ShowInTaskbar = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Ui.EnsureWindowVisible(this); Activate();
    }
    internal void ShowResetWeek() { ShowResets(); _resets.ShowWeek(); }
    internal void ShowResets() { ShowPanel(); ResetsTab.IsSelected = true; }
    /// <summary>From a calendar link: the account's details, or the account list when it is no longer tracked.</summary>
    internal void OpenAccount(Guid id)
    {
        OpenPage("Comptes");
        if (_service.State.Accounts.Any(a => a.Profile.Id == id)) OpenHistory(id);
    }
    internal void OpenPage(string page)
    {
        if (page == "Resets") { ShowResets(); return; }
        if (page == "Comptes") { ShowPanel(); AccountsTab.IsSelected = true; return; }
        if (page is not ("Général" or "Rappels" or "Canaux" or "Historique" or "Calendrier" or "Assistants" or "Application")) throw new ArgumentException(Loc.T("Page inconnue."));
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
        if (_model.HasGlobalReset) { ShowResets(); _resets.ShowAnnouncements(); return; }
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
    private void ShowVersion()
    {
        AppVersionLabel.Text = $"v{_updates.CurrentVersion}";
        AppVersionLabel.ToolTip = Loc.F("Version installée : {0}", _updates.CurrentVersion);
    }
    // Same check as Settings → Application: a newer version downloads and the usual banner offers to install it.
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || _checkingUpdate || _canClose) return;
        _checkingUpdate = true; _versionNotice.Stop(); CheckUpdateButton.IsEnabled = false;
        if (CheckUpdateButton.Content is FrameworkElement icon) UiMotion.Spin(icon);
        AppVersionLabel.Text = Loc.T("Recherche…");
        string text, hint;
        try
        {
            var result = await _updates.CheckDetailedAsync(_lifetime.Token);
            if (_canClose) return;
            if (result.IsVerifiedNow && result.Release is { } release)
            {
                text = Loc.F("v{0} disponible", release.Version);
                hint = _preferences.Current.DownloadUpdatesAutomatically ? Loc.T("Téléchargement et vérification en cours ; un bandeau proposera de l’installer.")
                    : Loc.T("Téléchargement automatique désactivé : installez-la depuis Réglages → Application.");
                AppVersionLabel.Text = text; AppVersionLabel.ToolTip = hint;
                if (_preferences.Current.DownloadUpdatesAutomatically) await _updates.PrepareAsync(release, _lifetime.Token);
            }
            else if (result.IsVerifiedNow)
            {
                text = Loc.T("À jour") + $" · v{_updates.CurrentVersion}";
                hint = Loc.F("Aucune version plus récente. Vérifié le {0:dd/MM/yyyy à HH:mm:ss}.", (result.VerifiedAt ?? DateTimeOffset.UtcNow).ToLocalTime());
            }
            else
            {
                text = Loc.T("Vérifiée récemment") + $" · v{_updates.CurrentVersion}";
                hint = result.Message + (result.NextCheckAt is { } next && next > DateTimeOffset.UtcNow ? "\n" + Loc.F("Nouvelle vérification possible dès le {0:dd/MM/yyyy à HH:mm:ss}.", next.ToLocalTime()) : "");
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { text = Loc.T("Recherche impossible") + $" · v{_updates.CurrentVersion}"; hint = Display.SafeText(error.Message, _preferences.Current.PrivacyMode); }
        finally { _checkingUpdate = false; if (!_canClose) CheckUpdateButton.IsEnabled = true; }
        if (_canClose) return;
        AppVersionLabel.Text = text; AppVersionLabel.ToolTip = hint; _versionNotice.Start();
    }
    private void UpdatePreparationChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdatePresentation);
    private void UpdatePresentation()
    {
        if (_canClose) return;
        PresentUpdate(_updates.Prepared?.Release.Version, _updates.IsPreparing);
    }
    internal void PresentUpdate(string? version, bool downloading)
    {
        UpdateBanner.Visibility = version is not null || downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeadline.Text = downloading ? Loc.T("Une mise à jour se télécharge…") : Loc.F("Version {0} prête à installer", version);
        UpdateHint.Text = downloading ? Loc.T("Le suivi continue en arrière-plan.") : _preferences.Current.InstallUpdatesAtStartup
            ? Loc.T("Maintenant, ou automatiquement au prochain démarrage du tracker.") : Loc.T("Téléchargée et vérifiée. Vos comptes et réglages sont conservés.");
        if (!_installingUpdate && _updates.Prepared?.AutomaticAttempted == true) UpdateHint.Text = Loc.T("Installation précédente interrompue. Vous pouvez réessayer.");
        UpdateNowButton.Visibility = version is not null && !downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateNowButton.IsEnabled = !_installingUpdate;
        UpdateNowButton.Content = _installingUpdate ? Loc.T("Installation…") : Loc.T("Mettre à jour et relancer");
    }
    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        try { await InstallReadyUpdateAsync(); }
        catch (Exception error) { if (!_canClose) ShowMessage(Loc.T("Mise à jour indisponible"), error.Message); }
    }
    internal async Task InstallReadyUpdateAsync()
    {
        if (_demo || _installingUpdate || _canClose) return;
        _installingUpdate = true; _automaticUpdates?.SetEnabled(false); UpdatePresentation();
        try
        {
            if (!await _updates.LaunchPreparedAsync(false, false, _lifetime.Token))
                throw new IOException(Loc.T("La mise à jour n’est plus prête. Recherchez-la à nouveau dans les réglages."));
            await ((App)System.Windows.Application.Current).ExitAsync();
        }
        finally
        {
            _installingUpdate = false;
            if (!_canClose) { UpdatePresentation(); _automaticUpdates?.SetEnabled(_preferences.Current.DownloadUpdatesAutomatically); }
        }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: FrameworkElement icon }) UiMotion.Spin(icon);
        await RefreshAsync();
    }
    public async Task RefreshAsync()
    {
        if (_refreshing || _service.State.IsBusy) return;
        _refreshing = true;
        try { await _service.RefreshAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsVisible && !_canClose) ShowMessage(Loc.T("Actualisation indisponible"), error.Message); }
        finally { _refreshing = false; }
    }
    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_canClose) ShowMessage(Loc.T("L’action n’a pas abouti"), error.Message); }
    }
    private static Guid Id(object sender) => (Guid)((FrameworkElement)sender).Tag;
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.ImportCurrentAccountAsync(_lifetime.Token));
    private void ManualCodexReset_Click(object sender, RoutedEventArgs e) => OpenManualCodexReset();
    /// <summary>Hides the dashboard notice only: the declaration stays applied and editable from the Codex header.</summary>
    private void DismissManualReset_Click(object sender, RoutedEventArgs e)
    {
        if (_preferences.Current.ManualCodexReset is { } reset) DismissNotice(sender, p => p with { DismissedManualCodexReset = reset.DeclaredAt });
    }
    /// <summary>Hides a notice until a new event replaces it; accounts keep their own estimate labels.</summary>
    private void DismissEventNotice_Click(object sender, RoutedEventArgs e)
    {
        string[] keys = Equals(((FrameworkElement)sender).Tag, "Advice") ? [.. new[] { _model.AdviceNoticeKey }.OfType<string>()] : _model.ExpectedResetNoticeKeys;
        if (keys.Length > 0) DismissNotice(sender, p => p with { DismissedNotices = _model.Dismissing(keys) });
    }
    private void DismissNotice(object sender, Func<TrackerPreferences, TrackerPreferences> change)
    {
        try { Commands.SavePreferences(change); }
        catch (Exception error) { ShowMessage(Loc.T("L’action n’a pas abouti"), error.Message); return; }
        // The banner collapses on the next model update: keep keyboard users in place.
        ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }
    internal ManualCodexResetWindow OpenManualCodexReset()
    {
        var existing = OwnedWindows.OfType<ManualCodexResetWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return existing; }
        var editor = new ManualCodexResetWindow(this); editor.Show(); return editor;
    }
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
    internal void ShowMessage(string title, string message) => new TrackerDialog(this, title, Display.SafeText(message, _preferences.Current.PrivacyMode), Loc.T("Fermer"), null).ShowDialog();
    public void ShowSettings()
    {
        ShowPanel(); _ = Settings; SettingsTab.IsSelected = true;
    }
    internal void OpenCalendar() => new CalendarWindow(this, _service, _preferences, Theme).ShowDialog();
    public void SaveScreenshot(string path, double dpi) => Ui.SaveScreenshot(this, path, dpi);
    public void SaveDetailsScreenshot(string path, double dpi)
    {
        if (!_model.IsDemo) throw new InvalidOperationException(Loc.T("Les captures de détails nécessitent le mode démonstration."));
        var account = _service.State.Accounts.FirstOrDefault(); if (account is null) return;
        var window = OpenHistory(account.Profile.Id); window.UpdateLayout(); Ui.SaveScreenshot(window, path, dpi); window.Close();
    }
    public void SaveSettingsScreenshot(string path, double dpi)
    {
        if (!_model.IsDemo) throw new InvalidOperationException(Loc.T("Les captures des réglages nécessitent le mode démonstration."));
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
