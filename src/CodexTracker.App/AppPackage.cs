using System.Runtime.InteropServices;
using System.Text;

namespace CodexTracker.App;

/// <summary>
/// The Microsoft Store edition runs from an MSIX package: its folder changes with every version and the Store updates it.
/// Paths handed to Windows or to other tools (startup, MCP, Claude Code, links) use the stable execution alias instead.
/// </summary>
internal static class AppPackage
{
    /// <summary>Declared in the package manifest (scripts/msix).</summary>
    internal const string Alias = "codex-tracker.exe";
    internal static bool IsPackaged { get; } = Detect();

    /// <summary>The executable path other programs should start: the alias when packaged, this executable otherwise.</summary>
    internal static string LaunchPath => IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", Alias)
        : Environment.ProcessPath!;

    private static bool Detect()
    {
        try
        {
            int length = 0;
            // APPMODEL_ERROR_NO_PACKAGE (15700) for a classic installation; otherwise the buffer is just too small.
            return GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch (Exception) { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, StringBuilder? name);
}
