using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexTracker.Codex;

/// <summary>Keep the store outside an inherited MSIX filesystem view.</summary>
public static class DesktopEnvironment
{
    public const string RelaunchArgument = "--desktop-storage";

    public static IReadOnlyList<string> FindRedirectedStores()
    {
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return [];
        return Directory.EnumerateDirectories(packages, "OpenAI.Codex_*")
            .Select(p => Path.Combine(p, "LocalCache", "Local", "CodexTracker"))
            .Where(p => File.Exists(Path.Combine(p, "settings.json")) || File.Exists(Path.Combine(p, "settings.json.bak")))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Package identity and TokenVirtualizationEnabled can both be absent while writes
    // are still redirected. Inspect an actual write handle, not environment variables.
    public static bool IsStorageRedirected(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.GetFullPath(Path.Combine(directory, $".location-{Guid.NewGuid():N}.tmp"));
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        var physical = new StringBuilder(32768);
        uint size = GetFinalPathNameByHandle(file.SafeFileHandle, physical, (uint)physical.Capacity, 0);
        if (size == 0 || size >= physical.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = physical.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) resolved = @"\\" + resolved[8..];
        else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
        return !string.Equals(path, resolved, StringComparison.OrdinalIgnoreCase);
    }

    public static int StartUnvirtualized(string executable, IEnumerable<string> arguments)
    {
        GetWindowThreadProcessId(GetShellWindow(), out var shellId);
        if (shellId == 0) throw new InvalidOperationException("Le bureau Windows n’est pas disponible. Lancez le tracker depuis le menu Démarrer.");
        using var shell = OpenProcess(0x0080, false, shellId); // PROCESS_CREATE_PROCESS only.
        if (shell.IsInvalid) throw new Win32Exception();
        nint bytes = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
        var attributes = Marshal.AllocHGlobal(bytes);
        var policy = Marshal.AllocHGlobal(nint.Size);
        bool initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception();
            initialized = true;
            // The desktop-app breakaway policy alone does not remove inherited file
            // virtualization on affected Windows builds. Inherit the desktop shell's
            // process context instead; no elevation, shell command or injected code.
            Marshal.WriteIntPtr(policy, shell.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (nint)0x20000, policy, nint.Size, 0, 0)) throw new Win32Exception();
            var startup = new StartupInfoEx { Attributes = attributes };
            startup.Info.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.Info.Flags = 1; // STARTF_USESHOWWINDOW; SW_HIDE, no helper console.
            var command = new StringBuilder(string.Join(" ", new[] { executable }.Concat(arguments).Select(QuoteArgument)));
            if (!CreateProcess(executable, command, 0, 0, false, 0x08080000, 0, null, ref startup, out var process))
                throw new Win32Exception(); // CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT
            try { return process.Id; }
            finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(policy);
        }
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize; public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public int Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, int flags, nint attribute, nint value, nint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
