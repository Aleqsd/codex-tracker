namespace CodexTracker.Codex;

public static class CodexLocator
{
    public static string? FindExecutable()
    {
        // The native app binary is preferable to npm's .cmd shim: no shell is required.
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var candidate = Path.Combine(path.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            var native = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (native is not null) return native;
        }
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai");
        return Directory.Exists(npm) ? Directory.EnumerateFiles(npm, "codex.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    }
}
