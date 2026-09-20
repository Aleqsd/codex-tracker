using System.Runtime.InteropServices;

namespace CodexTracker.App;

internal static class WindowsLifecycle
{
    internal const uint NoActivate = 0x0010, NoZOrder = 0x0004, NoSize = 0x0001;
    internal static readonly IntPtr Topmost = new(-1);

    public static PixelRect? Bounds(IntPtr handle)
        => GetWindowRect(handle, out var rect) ? rect.Value : null;
    public static double Scale(IntPtr handle) => WindowPlacement.Scale(GetDpiForWindow(handle));

    public static PixelRect WorkArea(PixelPoint anchor)
    {
        var point = new NativePoint { X = anchor.X, Y = anchor.Y };
        return ReadMonitor(MonitorFromPoint(point, 2));
    }
    public static PixelRect WorkArea(PixelRect bounds)
    {
        var rect = new NativeRect { Left = bounds.X, Top = bounds.Y, Right = bounds.Right, Bottom = bounds.Bottom };
        return ReadMonitor(MonitorFromRect(ref rect, 2));
    }
    private static PixelRect ReadMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.Work.Value;
        // GetSystemMetrics is physical in our PerMonitorV2 context; never mix in WPF DIPs.
        return new(0, 0, Math.Max(1, GetSystemMetrics(0)), Math.Max(1, GetSystemMetrics(1)));
    }
    public static void Move(IntPtr handle, PixelRect bounds, bool topmost = false)
        => SetWindowPos(handle, topmost ? Topmost : IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, NoActivate | (topmost ? 0 : NoZOrder));
    public static void MoveToMonitor(IntPtr handle, PixelPoint anchor)
        => SetWindowPos(handle, Topmost, anchor.X, anchor.Y, 0, 0, NoActivate | NoSize);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly PixelRect Value => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
