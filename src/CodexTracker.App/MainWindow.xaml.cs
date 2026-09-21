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
    private readonly ResetsView _resets;
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

    internal MainWindow(ITrackerService service, bool demo, PreferencesStore preferences, UpdateService updates)
    {
        _service = service; _preferences = preferences; _updates = updates;
        Reminders = new(service, preferences, demo);
        Commands = new(preferences, Reminders.Secrets);
        Theme = new ThemeManager(preferences);
        _model = new DashboardViewModel(demo, preferences);
        InitializeComponent();
        _resets = new ResetsView(_preferences, id => new CalendarWindow(this, _service, _preferences, Theme, id).ShowDialog(), () => _ = RefreshAsync()); ResetsTab.Content = _resets;
        if (demo) Title = "Codex Tracker (démo)";
        DataContext = _model;
        UpdateModel();
        _service.Changed += Service_Changed;
        _preferences.Changed += Preferences_Changed;
        Theme.Changed += Theme_Changed;
        _clockTimer.Tick += (_, _) => { _model.Tick(); _resets.Tick(); };
        Closing += OnClosing;
        SourceInitialized += (_, _) => { Ui.ConstrainInitialSize(this); ApplyChrome(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); if (e.Key == Key.F5) _ = RefreshAsync(); };
    }
    public async Task InitializeAsync()
    {
        await _service.InitializeAsync(_lifetime.Token);
        UpdateModel(); _clockTimer.Start();
    }
    private void UpdateModel()
    {
        if (_canClose) return;
        _model.Update(_service.State); _resets.Update(_service.State);
    }
    private void Service_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdateModel);
    private void Preferences_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdateModel);
    private void Theme_Changed(object? sender, EventArgs e) { ApplyChrome(); UpdateModel(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_canClose) { e.Cancel = true; Hide(); } }
    public void ShowPanel() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Ui.EnsureWindowVisible(this); Activate(); }
    internal void ShowResets() { ShowPanel(); ResetsTab.IsSelected = true; }
    internal void OpenPage(string page)
    {
        if (page == "Resets") { ShowResets(); return; }
        if (page == "Comptes") { ShowPanel(); AccountsTab.IsSelected = true; return; }
        if (page is not ("Général" or "Rappels" or "Canaux" or "Historique" or "Calendrier" or "Assistants" or "Application")) throw new ArgumentException("Page inconnue.");
        ShowPanel(); ShowSettings(); OwnedWindows.OfType<SettingsWindow>().First().ShowPage(page);
    }
    public void PrepareExit()
    {
        _canClose = true; _lifetime.Cancel(); _clockTimer.Stop();
        Assistant?.Dispose();
        foreach (Window child in OwnedWindows.Cast<Window>().ToArray()) child.Close();
        _service.Changed -= Service_Changed; _preferences.Changed -= Preferences_Changed; Theme.Changed -= Theme_Changed;
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
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
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
        var existing = OwnedWindows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new SettingsWindow(this, _preferences, _updates, Theme, _model.IsDemo).Show();
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
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
        var window = new SettingsWindow(this, _preferences, _updates, Theme, true); window.Show(); window.UpdateLayout(); Ui.SaveScreenshot(window, path, dpi); window.Close();
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
