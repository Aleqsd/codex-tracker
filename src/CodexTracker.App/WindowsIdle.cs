using System.Runtime.InteropServices;

namespace CodexTracker.App;

internal static class WindowsIdle
{
    public static TimeSpan Duration()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref input)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - input.Tick)) : TimeSpan.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Tick; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
