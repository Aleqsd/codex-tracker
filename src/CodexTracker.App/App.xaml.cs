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
    private Mcp.LocalServer? _assistantServer;
    private bool _exiting;
    private HwndSource? _messageSource;
    private static readonly uint ExitMessage = RegisterWindowMessage("CodexTracker.RequestExit.v1");
    private static readonly uint ShowMessage = RegisterWindowMessage("CodexTracker.RequestShow.v1");
    private static readonly uint OpenAccountMessage = RegisterWindowMessage("CodexTracker.OpenAccount.v1");
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    private readonly DispatcherTimer _environmentTimer = new(DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _taskbarCreated, _suspended;
    private DateTimeOffset _lastResume = DateTimeOffset.MinValue;
    internal bool IsDemo { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (await UpdateBootstrap.TryHandleAsync(e.Args)) { Shutdown(); return; }
        IsDemo = e.Args.Contains("--demo");
        var demoInstance = Array.IndexOf(e.Args, "--demo-instance");
        var demoSuffix = IsDemo && demoInstance >= 0 ? "." + Guid.Parse(e.Args[demoInstance + 1]).ToString("N") : "";
        var windowTitle = IsDemo ? "Codex Tracker (démo)" + demoSuffix : "Codex Tracker";
        if (e.Args.Contains("--exit"))
        {
            var running = FindWindow(null, windowTitle);
            if (running != IntPtr.Zero) PostMessage(running, ExitMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown(); return;
        }
        if (e.Args.Contains("--screenshot")) RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        _mutex = new Mutex(true, IsDemo ? "Local\\CodexTracker.Demo" + demoSuffix : "Local\\CodexTracker", out bool created);
        if (!created)
        {
            var handle = FindWindow(null, windowTitle);
            if (handle != IntPtr.Zero && !e.Args.Contains("--background"))
            {
                GetWindowThreadProcessId(handle, out var processId);
                AllowSetForegroundWindow(processId);
                // The running WPF dispatcher must call Window.Show. Showing only the HWND
                // leaves a never-shown background window without its visual tree (black).
                if (AccountLink.Parse(e.Args) is { } link) { var (first, second) = AccountLink.Pack(link); PostMessage(handle, OpenAccountMessage, first, second); }
                else PostMessage(handle, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            }
            Shutdown(); return;
        }
        try
        {
            // Every demo capture shows final states: no half-filled gauge or fading panel.
            if (new[] { "--screenshot", "--peek-screenshot", "--details-screenshot", "--settings-screenshot" }.Any(e.Args.Contains)) UiMotion.Suppressed = true;
            if (e.Args.Contains("--preview"))
            {
                if (!IsDemo) throw new ArgumentException(Loc.T("Les aperçus nécessitent --demo."));
                UiMotion.Suppressed = true;
                PreviewClock.Fixed = DateTimeOffset.Parse("2026-09-21T12:00:00Z");
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            }
            var preferences = new PreferencesStore(persistent: !IsDemo);
            // The language is applied before any window exists; changing it takes effect at the next start.
            int languageArgument = Array.IndexOf(e.Args, "--language");
            var language = languageArgument >= 0 && languageArgument + 1 < e.Args.Length
                ? e.Args[languageArgument + 1] == "en" ? AppLanguage.English : AppLanguage.French
                : e.Args.Contains("--preview") ? AppLanguage.French : preferences.Current.Language;
            Loc.Register(EnglishApp.All); Loc.Use(language);
            // Display formatting passes Loc.Culture explicitly: a culture set in this async method would not outlive its flow.
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = Loc.Culture;
            if (IsDemo && demoInstance >= 0) preferences.Update(p => p with { McpEnabled = true });
            var updates = new UpdateService();
            updates.SetIncludePrereleases(preferences.Current.IncludePrereleaseUpdates);
            if (!IsDemo)
            {
                await updates.LoadPreparedAsync();
                // MCP startup must remain available within its connection deadline. Health probes
                // never trigger another installation; the persisted claim also prevents rollback loops.
                if (UpdateService.CanSelfUpdate && preferences.Current.InstallUpdatesAtStartup &&
                    !e.Args.Contains("--assistant-start") && !e.Args.Contains("--update-health"))
                {
                    try
                    {
                        if (await updates.LaunchPreparedAsync(true, e.Args.Contains("--background")))
                        { updates.Dispose(); await ExitAsync(); return; }
                    }
                    catch (Exception) { /* Continue opening the working version; allow a manual retry. */ }
                }
            }
            _service = IsDemo ? new DemoTrackerService(e.Args.Contains("--demo-advice"), e.Args.Contains("--demo-resets"), e.Args.Contains("--demo-global-resets"), e.Args.Contains("--demo-claude"), e.Args.Contains("--demo-manual-reset")) : new Codex.TrackerService(options: new()
            {
                RedirectedDataDirectories = Codex.DesktopEnvironment.FindRedirectedStores(),
                GlobalResetMonitoringEnabled = () => preferences.Current.MonitorGlobalResets,
                ManualCodexResetProvider = () => preferences.Current.ManualCodexReset,
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
            if (IsDemo && e.Args.Contains("--demo-manual-reset")) preferences.Update(p => p with { ManualCodexReset = new(PreviewClock.UtcNow.AddMinutes(-30), PreviewClock.UtcNow) });
            var window = new MainWindow(_service, IsDemo, preferences, updates);
            if (IsDemo && e.Args.Contains("--demo-update")) window.PresentUpdate("0.9.0", false);
            window.Title = windowTitle;
            MainWindow = window;
            var handle = new WindowInteropHelper(window).EnsureHandle();
            _messageSource = HwndSource.FromHwnd(handle);
            _messageSource?.AddHook(HandleWindowMessage);
            _tray = new TrayController(window, _service, preferences, ExitAsync);
            _environmentTimer.Tick += EnvironmentTimerTick;
            SystemEvents.PowerModeChanged += PowerModeChanged;
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            if (e.Args.Contains("--preview") || e.Args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a.EndsWith("-screenshot", StringComparison.Ordinal)))
            {
                // Previews and captures render off-screen without activation: they never take focus from the user's work or game.
                window.Offscreen = true; window.ShowActivated = false; window.ShowInTaskbar = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -32000;
            }
            if (!e.Args.Contains("--background")) window.Show();
            await window.InitializeAsync();
            if (_exiting) return;
            if (AccountLink.Parse(e.Args) is { } account) window.OpenAccount(account);
            if (!IsDemo || demoInstance >= 0)
            {
                try
                {
                    window.Assistant = new Mcp.TrackerControl(window, IsDemo);
                    _assistantServer = new(window, window.Assistant, e.Args);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { window.AssistantError = Loc.T("MCP indisponible : journal local illisible. Le suivi reste actif."); }
            }
            UpdateBootstrap.MarkHealthy(e.Args);
            if (e.Args.Contains("--preview"))
            {
                await RenderPreview(window, e.Args); await ExitAsync(); return;
            }
            if (!e.Args.Contains("--assistant-start") && !window.IsVisible && (!_service.State.OnboardingComplete || _service.State.Accounts.Count == 0)) window.ShowPanel();
            int imageArgument = Array.IndexOf(e.Args, "--screenshot");
            if (imageArgument >= 0 && imageArgument + 1 < e.Args.Length)
            {
                // Render the fictional demo only; never export private account data accidentally.
                if (!IsDemo) throw new InvalidOperationException(Loc.F("{0} nécessite --demo.", "--screenshot"));
                int dpiArgument = Array.IndexOf(e.Args, "--dpi");
                double dpi = dpiArgument >= 0 && dpiArgument + 1 < e.Args.Length && double.TryParse(e.Args[dpiArgument + 1], out var parsed) ? Math.Clamp(parsed, 96, 288) : 96;
                window.ShowPanel();
                await Task.Delay(450);
                window.SaveScreenshot(Path.GetFullPath(e.Args[imageArgument + 1]), dpi);
            }
            int peekArgument = Array.IndexOf(e.Args, "--peek-screenshot");
            if (peekArgument >= 0 && peekArgument + 1 < e.Args.Length)
            {
                if (!IsDemo) throw new InvalidOperationException(Loc.F("{0} nécessite --demo.", "--peek-screenshot"));
                int dpiArgument = Array.IndexOf(e.Args, "--dpi");
                double dpi = dpiArgument >= 0 && dpiArgument + 1 < e.Args.Length && double.TryParse(e.Args[dpiArgument + 1], out var parsed) ? Math.Clamp(parsed, 96, 288) : 96;
                _tray.SavePeekScreenshot(Path.GetFullPath(e.Args[peekArgument + 1]), dpi);
            }
            foreach (var option in new[] { "--details-screenshot", "--settings-screenshot" })
            {
                int argument = Array.IndexOf(e.Args, option);
                if (argument < 0 || argument + 1 >= e.Args.Length) continue;
                if (!IsDemo) throw new InvalidOperationException(Loc.F("{0} nécessite --demo.", option));
                if (option == "--details-screenshot") window.SaveDetailsScreenshot(Path.GetFullPath(e.Args[argument + 1]), 96);
                else window.SaveSettingsScreenshot(Path.GetFullPath(e.Args[argument + 1]), 96);
            }
            if (e.Args.Contains("--smoke-test")) { await Task.Delay(400); await ExitAsync(); }
        }
        catch (Exception error)
        {
            if (_exiting) return;
            System.Windows.MessageBox.Show(Loc.F("Codex Tracker n’a pas pu démarrer.\n\n{0}", error.Message), "Codex Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (_assistantServer is not null) await _assistantServer.DisposeAsync();
        if (MainWindow is MainWindow window) window.PrepareExit();
        _tray?.Dispose();
        try { if (_service is not null) await _service.DisposeAsync(); }
        finally { _mutex?.Dispose(); Shutdown(); }
    }

    private static async Task RenderPreview(MainWindow window, string[] args)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var page = Option("--view", "Comptes");
        if (page == "Semaine") window.ShowResetWeek(); else window.OpenPage(page);
        Window target = window;
        if (Option("--size", "normal") == "compact") { target.Width = Math.Max(target.MinWidth, 660); target.Height = Math.Max(target.MinHeight, 500); }
        var dpi = double.Parse(Option("--dpi", "96"), System.Globalization.CultureInfo.InvariantCulture);
        if (dpi is not (96 or 120 or 144 or 192)) throw new ArgumentException(Loc.T("DPI : 96, 120, 144 ou 192."));
        await Task.Delay(200); target.UpdateLayout(); Ui.SaveScreenshot(target, Path.GetFullPath(Option("--preview", "artifacts/preview.png")), dpi);
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
        if ((uint)message == OpenAccountMessage)
        {
            handled = true;
            var account = AccountLink.Unpack(wParam, lParam);
            Dispatcher.InvokeAsync(() => { if (!_exiting && MainWindow is MainWindow window) window.OpenAccount(account); });
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
        catch (Exception error) { if (!_exiting && MainWindow is MainWindow window && window.IsVisible) window.ShowMessage(Loc.T("Reprise du suivi"), error.Message); }
    });

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
