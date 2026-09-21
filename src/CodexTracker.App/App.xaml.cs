using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using CodexTracker.App.Updates;
using Microsoft.Win32;
using System.Windows.Threading;

namespace CodexTracker.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private ITrackerService? _service;
    private TrayController? _tray;
    private bool _exiting;
    private HwndSource? _messageSource;
    private static readonly uint ExitMessage = RegisterWindowMessage("CodexTracker.RequestExit.v1");
    private static readonly uint ShowMessage = RegisterWindowMessage("CodexTracker.RequestShow.v1");
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private readonly DispatcherTimer _environmentTimer = new(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _taskbarCreated, _suspended;
    private DateTimeOffset _lastResume = DateTimeOffset.MinValue;
    internal bool IsDemo { get; private set; }
    internal void ShowTestNotification() => _tray?.ShowTestNotification();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (await UpdateBootstrap.TryHandleAsync(e.Args)) { Shutdown(); return; }
        IsDemo = e.Args.Contains("--demo");
        if (e.Args.Contains("--exit"))
        {
            var running = FindWindow(null, IsDemo ? "Codex Tracker (démo)" : "Codex Tracker");
            if (running != IntPtr.Zero) PostMessage(running, ExitMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown(); return;
        }
        if (e.Args.Contains("--screenshot")) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        _mutex = new Mutex(true, IsDemo ? "Local\\CodexTracker.Demo" : "Local\\CodexTracker", out bool created);
        if (!created)
        {
            var handle = FindWindow(null, IsDemo ? "Codex Tracker (démo)" : "Codex Tracker");
            if (handle != IntPtr.Zero && !e.Args.Contains("--background"))
            {
                GetWindowThreadProcessId(handle, out var processId);
                AllowSetForegroundWindow(processId);
                // The running WPF dispatcher must call Window.Show. Showing only the HWND
                // leaves a never-shown background window without its visual tree (black).
                PostMessage(handle, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            }
            Shutdown(); return;
        }
        try
        {
            var preferences = new PreferencesStore(persistent: !IsDemo);
            _service = IsDemo ? new DemoTrackerService(e.Args.Contains("--demo-advice")) : new Codex.TrackerService(options: new()
            {
                RefreshIntervalProvider = () =>
                {
                    var p = preferences.Current;
                    return RefreshPolicy.Interval(p.RefreshMinutes, p.AdaptiveRefresh, WindowsIdle.Duration());
                }
            });
            if (IsDemo)
            {
                int themeArgument = Array.IndexOf(e.Args, "--theme");
                if (themeArgument >= 0 && themeArgument + 1 < e.Args.Length)
                    preferences.Update(p => p with { ThemeMode = e.Args[themeArgument + 1] == "light" ? CodexTracker.App.ThemeMode.Light : CodexTracker.App.ThemeMode.Dark });
            }
            var window = new MainWindow(_service, IsDemo, preferences, new UpdateService());
            MainWindow = window;
            var handle = new WindowInteropHelper(window).EnsureHandle();
            _messageSource = HwndSource.FromHwnd(handle);
            _messageSource?.AddHook(HandleWindowMessage);
            _tray = new TrayController(window, _service, preferences, ExitAsync);
            _environmentTimer.Tick += EnvironmentTimerTick;
            SystemEvents.PowerModeChanged += PowerModeChanged;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            if (!e.Args.Contains("--background") || IsDemo) window.Show();
            await window.InitializeAsync();
            if (_exiting) return;
            UpdateBootstrap.MarkHealthy(e.Args);
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
            int peekArgument = Array.IndexOf(e.Args, "--peek-screenshot");
            if (peekArgument >= 0 && peekArgument + 1 < e.Args.Length)
            {
                if (!IsDemo) throw new InvalidOperationException("--peek-screenshot nécessite --demo.");
                _tray.SavePeekScreenshot(Path.GetFullPath(e.Args[peekArgument + 1]));
            }
            foreach (var option in new[] { "--details-screenshot", "--settings-screenshot" })
            {
                int argument = Array.IndexOf(e.Args, option);
                if (argument < 0 || argument + 1 >= e.Args.Length) continue;
                if (!IsDemo) throw new InvalidOperationException($"{option} nécessite --demo.");
                if (option == "--details-screenshot") window.SaveDetailsScreenshot(Path.GetFullPath(e.Args[argument + 1]), 96);
                else window.SaveSettingsScreenshot(Path.GetFullPath(e.Args[argument + 1]), 96);
            }
            if (e.Args.Contains("--smoke-test")) { await Task.Delay(400); await ExitAsync(); }
        }
        catch (Exception error)
        {
            if (_exiting) return;
            System.Windows.MessageBox.Show($"Codex Tracker n’a pas pu démarrer.\n\n{error.Message}", "Codex Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    internal async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _environmentTimer.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
        _messageSource?.RemoveHook(HandleWindowMessage);
        if (MainWindow is MainWindow window) window.PrepareExit();
        _tray?.Dispose();
        try { if (_service is not null) await _service.DisposeAsync(); }
        finally { _mutex?.Dispose(); Shutdown(); }
    }

    private IntPtr HandleWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == ShowMessage)
        {
            handled = true;
            Dispatcher.InvokeAsync(() =>
            {
                if (!_exiting && MainWindow is MainWindow window) window.ShowPanel();
            });
        }
        if ((uint)message == ExitMessage)
        {
            handled = true;
            Dispatcher.InvokeAsync(async () => await ExitAsync());
        }
        // WPF must still process WM_DPICHANGED and its suggested rectangle before our correction.
        if ((uint)message == TaskbarCreatedMessage || message is 0x007E or 0x02E0 || message == 0x001A && wParam.ToInt64() == 0x002F)
            QueueEnvironmentChange((uint)message == TaskbarCreatedMessage);
        return IntPtr.Zero;
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() => QueueEnvironmentChange());

    private void QueueEnvironmentChange(bool taskbarCreated = false)
    {
        if (_exiting) return;
        _taskbarCreated |= taskbarCreated;
        _environmentTimer.Stop();
        _environmentTimer.Start();
    }

    private void EnvironmentTimerTick(object? sender, EventArgs e)
    {
        _environmentTimer.Stop();
        if (_exiting) return;
        if (MainWindow is MainWindow window) Ui.HandleEnvironmentChanged(window);
        _tray?.HandleEnvironmentChanged(_taskbarCreated);
        _taskbarCreated = false;
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.InvokeAsync(async () =>
    {
        if (_exiting || _service is null) return;
        try
        {
            if (e.Mode == PowerModes.Suspend)
            {
                _suspended = true;
                _environmentTimer.Stop();
                _tray?.OnSuspend();
                await _service.SuspendAsync();
            }
            else if (e.Mode == PowerModes.Resume)
            {
                if (!_suspended && DateTimeOffset.UtcNow - _lastResume < TimeSpan.FromSeconds(2)) return;
                _suspended = false;
                _lastResume = DateTimeOffset.UtcNow;
                _tray?.OnResume();
                QueueEnvironmentChange();
                await _service.ResumeAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_exiting && MainWindow is MainWindow window && window.IsVisible) window.ShowMessage("Reprise du suivi", error.Message); }
    });

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
