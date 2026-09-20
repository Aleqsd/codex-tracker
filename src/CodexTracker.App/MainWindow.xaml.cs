using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CodexTracker.App;

public partial class MainWindow : Window
{
    private readonly ITrackerService _service;
    private readonly DashboardViewModel _model;
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _canClose;
    private bool _refreshing;

    public MainWindow(ITrackerService service, bool demo)
    {
        _service = service;
        _model = new DashboardViewModel(demo);
        InitializeComponent();
        if (demo) Title = "Codex Tracker (démo)";
        DataContext = _model;
        _model.Update(service.State);
        _service.Changed += Service_Changed;
        _clockTimer.Tick += (_, _) => _model.Tick();
        Closing += OnClosing;
        SourceInitialized += (_, _) => { int dark = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, 4); int rounded = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref rounded, 4); };
        SystemEvents.PowerModeChanged += PowerModeChanged;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); if (e.Key == Key.F5) _ = RefreshAsync(); };
    }

    public async Task InitializeAsync()
    {
        await _service.InitializeAsync(_lifetime.Token);
        _model.Update(_service.State);
        _clockTimer.Start();
    }
    private void Service_Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => _model.Update(_service.State));
    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(async () => await RefreshAsync()); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!_canClose) { e.Cancel = true; Hide(); } }
    public void ShowPanel() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    public void PrepareExit()
    {
        _canClose = true; _lifetime.Cancel(); _clockTimer.Stop();
        _service.Changed -= Service_Changed; SystemEvents.PowerModeChanged -= PowerModeChanged;
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    public async Task RefreshAsync()
    {
        if (_refreshing || _service.State.IsBusy) return;
        _refreshing = true;
        try { await _service.RefreshAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (IsVisible) ShowMessage("Actualisation indisponible", error.Message); }
        finally { _refreshing = false; }
    }
    private async Task RunAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowMessage("L’action n’a pas abouti", error.Message); }
    }
    private static Guid Id(object sender) => (Guid)((FrameworkElement)sender).Tag;
    private async void Select_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.SelectAccountAsync(Id(sender), _lifetime.Token));
    private async void Import_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.ImportCurrentAccountAsync(_lifetime.Token));
    private async void Onboarding_Click(object sender, RoutedEventArgs e) => await RunAsync(() => _service.CompleteOnboardingAsync(_lifetime.Token));
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var id = Id(sender); var account = _service.State.Accounts.First(a => a.Profile.Id == id);
        var dialog = new TrackerDialog(this, "Retirer ce compte du suivi ?", $"{account.Profile.Email}\n\nSon dernier relevé sera supprimé du tracker. Il réapparaîtra automatiquement la prochaine fois que vous ouvrirez ce compte dans Codex.", "Retirer du suivi", "Annuler");
        if (dialog.ShowDialog() == true) await RunAsync(() => _service.RemoveAccountAsync(id, _lifetime.Token));
    }
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        var vm = _model.Accounts.First(a => a.Id == Id(sender));
        ShowMessage("Détails du compte", vm.AllDetails);
    }
    internal void ShowMessage(string title, string message) => new TrackerDialog(this, title, message, "Fermer", null).ShowDialog();
    public void ShowSettings()
    {
        var dialog = new TrackerDialog(this, "À votre rythme", "Le tracker détecte les changements de compte toutes les deux secondes et actualise les quotas du compte actif toutes les deux minutes. Fermer le panneau conserve l’icône près de l’horloge.", "Enregistrer", "Annuler");
        var startup = new CheckBox { Content = "Démarrer avec Windows", IsChecked = StartupSettings.IsEnabled, Margin = new Thickness(0, 16, 0, 10), IsEnabled = !_model.IsDemo };
        dialog.Extra.Children.Add(startup);
        var hint = new TextBlock { Text = "Pour toujours voir le quota : ouvrez ^ près de l’horloge, puis faites glisser l’icône du tracker dans la zone visible.", TextWrapping = TextWrapping.Wrap, Foreground = Display.Muted, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
        dialog.Extra.Children.Add(hint);
        var quit = new Button { Content = "Quitter Codex Tracker", Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        quit.Click += async (_, _) => { dialog.Close(); await ((App)System.Windows.Application.Current).ExitAsync(); };
        dialog.Extra.Children.Add(quit);
        if (dialog.ShowDialog() == true && !_model.IsDemo)
        {
            try { StartupSettings.SetEnabled(startup.IsChecked == true); }
            catch (Exception error) { ShowMessage("Réglage non enregistré", error.Message); }
        }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    public void SaveScreenshot(string path, double dpi)
    {
        UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi / 96), (int)Math.Ceiling(ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
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
