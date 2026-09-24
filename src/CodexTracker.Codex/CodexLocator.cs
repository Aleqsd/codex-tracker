using System.ComponentModel;
using System.Diagnostics;

namespace CodexTracker.Codex;

public static class CodexLocator
{
    public static string? FindExecutable()
        => FindExecutables().FirstOrDefault();

    internal static IReadOnlyList<string> FindExecutables()
        => FindExecutables(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static string? FindExecutable(string? pathEnvironment, string localApplicationData, string applicationData)
        => FindExecutables(pathEnvironment, localApplicationData, applicationData).FirstOrDefault();

    internal static IReadOnlyList<string> FindExecutables(string? pathEnvironment, string localApplicationData, string applicationData)
    {
        var candidates = new List<string>();
        // The native app binary is preferable to npm's .cmd shim: no shell is required.
        foreach (var path in (pathEnvironment ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(path.Trim().Trim('"'), "codex.exe"));
                if (File.Exists(candidate)) candidates.Add(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException) { }
        }
        candidates.AddRange(FindUnder(Path.Combine(localApplicationData, "OpenAI", "Codex", "bin")));
        candidates.AddRange(FindUnder(Path.Combine(applicationData, "npm", "node_modules", "@openai")));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Retry only failures to create the process. Once a process starts, protocol or account errors
    // must not launch another reader or conceal the original failure. Never elevate or change ACLs.
    internal static Process StartProcess(IReadOnlyList<string> executables, Func<string, Process> start)
    {
        int? lastError = null;
        foreach (var executable in executables)
        {
            try { return start(executable); }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3 or 5 or 126 or 193 or 216 or 740)
            {
                if (lastError is null or 2 or 3 || ex.NativeErrorCode is not (2 or 3))
                    lastError = ex.NativeErrorCode;
            }
        }
        if (lastError is null or 2 or 3)
            throw new TrackerException("Le service Codex est introuvable. Installez ou mettez à jour Codex, puis relancez le tracker.", CodexFailureCode.CodexNotFound);
        throw new TrackerException($"Windows ne peut pas démarrer le service Codex (erreur {lastError}). Ouvrez ou mettez à jour Codex, puis actualisez les quotas. Dernier relevé conservé.", CodexFailureCode.CodexLaunchFailed);
    }

    private static IReadOnlyList<string> FindUnder(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            return Directory.EnumerateFiles(directory, "codex.exe", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return []; }
    }
}
