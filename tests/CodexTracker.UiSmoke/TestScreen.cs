using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CodexTracker.App;
using Screen = System.Windows.Forms.Screen;

/// <summary>
/// Real test windows open on a chosen screen so they do not cover the working one:
/// CODEX_TRACKER_TEST_SCREEN = right (default), left, primary or a device name such as DISPLAY1.
/// A missing screen falls back to the primary one. Windows are moved just before they appear.
/// </summary>
internal static class TestScreen
{
    private const int CallWindowProcedureHook = 4;
    private const uint WindowPositionChanging = 0x0046, ShowWindowFlag = 0x0040;
    private static HookProcedure? _procedure;
    private static IntPtr _hook;

    public static Screen Target { get; } = Resolve(Environment.GetEnvironmentVariable("CODEX_TRACKER_TEST_SCREEN") ?? "right");
    public static System.Drawing.Rectangle Area => Target.WorkingArea;

    public static void Install()
    {
        Console.WriteLine($"Test windows on {Target.DeviceName.TrimStart('\\', '.')}{(Target.Primary ? " (primary)" : "")}");
        if (Target.Primary) return;
        _procedure = Intercept;
        _hook = SetWindowsHookEx(CallWindowProcedureHook, _procedure, IntPtr.Zero, GetCurrentThreadId());
    }

    public static void Uninstall()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static Screen Resolve(string name)
    {
        var primary = Screen.PrimaryScreen!;
        return name.Trim().ToLowerInvariant() switch
        {
            "primary" => primary,
            "right" => Screen.AllScreens.Where(s => s.Bounds.Left >= primary.Bounds.Right).OrderBy(s => s.Bounds.Left).FirstOrDefault() ?? primary,
            "left" => Screen.AllScreens.Where(s => s.Bounds.Right <= primary.Bounds.Left).OrderByDescending(s => s.Bounds.Right).FirstOrDefault() ?? primary,
            var device => Screen.AllScreens.FirstOrDefault(s => s.DeviceName.TrimStart('\\', '.').Equals(device.TrimStart('\\', '.'), StringComparison.OrdinalIgnoreCase)) ?? primary,
        };
    }

    // WPF remembers the position it computes (centred on the mouse's screen, or the system default) and applies it
    // again while showing. The window is moved at its first appearance, once WPF has finished creating it, still hidden:
    // WPF records that move and the window never shows on the primary screen.
    private static IntPtr Intercept(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = Marshal.PtrToStructure<CallWindowProcedureMessage>(lParam);
            if (message.Message == WindowPositionChanging && message.LParam != IntPtr.Zero && !IsWindowVisible(message.Window) &&
                (Marshal.PtrToStructure<WindowPosition>(message.LParam).Flags & ShowWindowFlag) != 0 &&
                ApplicationWindow(message.Window) is { } window && WindowsLifecycle.Bounds(message.Window) is { } bounds)
            {
                var source = WindowsLifecycle.WorkArea(bounds);
                var target = WindowsLifecycle.WorkArea(new PixelPoint(Area.X + Area.Width / 2, Area.Y + Area.Height / 2));
                if (source != target)
                {
                    var moved = new PixelRect(target.X + Math.Clamp(bounds.X - source.X, 0, Math.Max(0, target.Width - bounds.Width)),
                        target.Y + Math.Clamp(bounds.Y - source.Y, 0, Math.Max(0, target.Height - bounds.Height)), bounds.Width, bounds.Height);
                    // A later first WPF Show() would otherwise centre it again on the mouse's screen.
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    WindowsLifecycle.Move(message.Window, moved);
                    // The pending show must not restore the previous position either.
                    var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
                    position.X = moved.X; position.Y = moved.Y;
                    Marshal.StructureToPtr(position, message.LParam, false);
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // Only WPF windows: popups, tooltips and drop-downs follow their own placement.
    private static Window? ApplicationWindow(IntPtr handle) =>
        Application.Current?.Windows.Cast<Window>().FirstOrDefault(window => new WindowInteropHelper(window).Handle == handle);

    private delegate IntPtr HookProcedure(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    private struct CallWindowProcedureMessage { public IntPtr LParam, WParam; public uint Message; public IntPtr Window; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hook, HookProcedure procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
}
