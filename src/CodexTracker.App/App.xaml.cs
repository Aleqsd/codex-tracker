using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CodexTracker.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private ITrackerService? _service;
    private TrayController? _tray;
    private bool _exiting;
    internal bool IsDemo { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsDemo = e.Args.Contains("--demo");
        if (e.Args.Contains("--screenshot")) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        _mutex = new Mutex(true, IsDemo ? "Local\\CodexTracker.Demo" : "Local\\CodexTracker", out bool created);
        if (!created)
        {
            var handle = FindWindow(null, IsDemo ? "Codex Tracker (démo)" : "Codex Tracker");
            if (handle != IntPtr.Zero) { ShowWindow(handle, 9); SetForegroundWindow(handle); }
            Shutdown(); return;
        }
        try
        {
            _service = IsDemo ? new DemoTrackerService() : new Codex.TrackerService();
            var window = new MainWindow(_service, IsDemo);
            MainWindow = window;
            _tray = new TrayController(window, _service, ExitAsync);
            if (!e.Args.Contains("--background") || IsDemo) window.Show();
            await window.InitializeAsync();
            if (!window.IsVisible && (!_service.State.OnboardingComplete || _service.State.Accounts.Count == 0)) window.ShowPanel();
            int imageArgument = Array.IndexOf(e.Args, "--screenshot");
            if (imageArgument >= 0 && imageArgument + 1 < e.Args.Length)
            {
                // Render the fictional demo only; never export private account data accidentally.
                if (!IsDemo) throw new InvalidOperationException("--screenshot nécessite --demo.");
                int dpiArgument = Array.IndexOf(e.Args, "--dpi");
                double dpi = dpiArgument >= 0 && dpiArgument + 1 < e.Args.Length && double.TryParse(e.Args[dpiArgument + 1], out var parsed) ? Math.Clamp(parsed, 96, 288) : 96;
                window.ShowPanel();
                await Task.Delay(450);
                window.SaveScreenshot(Path.GetFullPath(e.Args[imageArgument + 1]), dpi);
            }
            if (e.Args.Contains("--smoke-test")) { await Task.Delay(400); await ExitAsync(); }
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show($"Codex Tracker n’a pas pu démarrer.\n\n{error.Message}", "Codex Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    internal async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (MainWindow is MainWindow window) window.PrepareExit();
        _tray?.Dispose();
        if (_service is not null) await _service.DisposeAsync();
        _mutex?.Dispose();
        Shutdown();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
}
