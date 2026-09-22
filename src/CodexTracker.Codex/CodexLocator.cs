namespace CodexTracker.Codex;

public static class CodexLocator
{
    public static string? FindExecutable()
        => FindExecutable(Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static string? FindExecutable(string? pathEnvironment, string localApplicationData, string applicationData)
    {
        // The native app binary is preferable to npm's .cmd shim: no shell is required.
        foreach (var path in (pathEnvironment ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(path.Trim().Trim('"'), "codex.exe"));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException) { }
        }
        return FindUnder(Path.Combine(localApplicationData, "OpenAI", "Codex", "bin"))
            ?? FindUnder(Path.Combine(applicationData, "npm", "node_modules", "@openai"));
    }

    private static string? FindUnder(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return null;
            return Directory.EnumerateFiles(directory, "codex.exe", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }
}
