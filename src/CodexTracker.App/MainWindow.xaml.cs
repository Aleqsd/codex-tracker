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
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _canClose, _refreshing, _updatingSort;
    internal ThemeManager Theme { get; }
    internal PreferencesStore Preferences => _preferences;

    internal MainWindow(ITrackerService service, bool demo, PreferencesStore preferences, UpdateService updates)
    {
        _service = service; _preferences = preferences; _updates = updates;
        Theme = new ThemeManager(preferences);
        _model = new DashboardViewModel(demo, preferences);
        InitializeComponent();
        if (demo) Title = "Codex Tracker (démo)";
        DataContext = _model;
        UpdateModel();
        _service.Changed += Service_Changed;
        _preferences.Changed += Preferences_Changed;
        Theme.Changed += Theme_Changed;
        _clockTimer.Tick += (_, _) => _model.Tick();
        Closing += OnClosing;
        SourceInitialized += (_, _) => { Ui.ConstrainInitialSize(this); ApplyChrome(); };
        SystemEvents.PowerModeChanged += PowerModeChanged;
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
        _model.Update(_service.State);
        _updatingSort = true; SortSelector.SelectedValue = _preferences.Current.SortMode; _updatingSort = false;
    }
    private void Service_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdateModel);
    private void Preferences_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdateModel);
    private void Theme_Changed(object? sender, EventArgs e) { ApplyChrome(); UpdateModel(); }
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(async () => await RefreshAsync()); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_canClose) { e.Cancel = true; Hide(); } }
    public void ShowPanel() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    public void PrepareExit()
    {
        _canClose = true; _lifetime.Cancel(); _clockTimer.Stop();
        foreach (Window child in OwnedWindows.Cast<Window>().ToArray()) child.Close();
        _service.Changed -= Service_Changed; _preferences.Changed -= Preferences_Changed; Theme.Changed -= Theme_Changed;
        SystemEvents.PowerModeChanged -= PowerModeChanged; Theme.Dispose(); _updates.Dispose();
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
    private void Privacy_Click(object sender, RoutedEventArgs e) => UpdatePreferences(p => p with { PrivacyMode = !p.PrivacyMode });
    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSort && SortSelector.SelectedValue is SortMode mode) UpdatePreferences(p => p with { SortMode = mode });
    }
    private void UpdatePreferences(Func<TrackerPreferences, TrackerPreferences> update)
    {
        try { _preferences.Update(update); }
        catch (Exception error) { ShowMessage("Réglage non enregistré", error.Message); }
    }
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
    private async void Select_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.SelectAccountAsync(Id(sender), _lifetime.Token));
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.ImportCurrentAccountAsync(_lifetime.Token));
    private async void Onboarding_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.CompleteOnboardingAsync(_lifetime.Token));
    private void Details_Click(object sender, RoutedEventArgs e) => OpenHistory(Id(sender));
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
