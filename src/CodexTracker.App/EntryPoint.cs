namespace CodexTracker.App;

internal static class EntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--mcp")) return Mcp.McpHost.RunAsync(args).GetAwaiter().GetResult();
        // A restart waits for the previous instance to release the single-instance lock.
        var relaunch = Array.IndexOf(args, "--relaunch-after");
        if (relaunch >= 0 && relaunch + 1 < args.Length && int.TryParse(args[relaunch + 1], out var previous))
        {
            try { using var process = System.Diagnostics.Process.GetProcessById(previous); process.WaitForExit(15000); }
            catch (ArgumentException) { /* Already closed. */ }
            catch (InvalidOperationException) { }
        }
        if (args.Contains("--unregister-notifications"))
        {
            try { WindowsToasts.Unregister(); } catch (Exception) { /* Uninstallation continues without notification cleanup. */ }
            return 0;
        }
        if (args.Contains("--claude-statusline") || args.Contains("--claude-session-start"))
        {
            try
            {
                var dataOption = Array.IndexOf(args, "--claude-data-directory");
                var directory = dataOption >= 0 && dataOption + 1 < args.Length ? args[dataOption + 1] : new Codex.TrackerServiceOptions().DataDirectory;
                if (!System.IO.Path.IsPathFullyQualified(directory)) return 0;
                var location = Codex.ClaudeCodeLocation.Resolve(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                using var input = new System.IO.StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
                using var output = new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                new Codex.ClaudeCodeObservations(directory, location).RunAsync(input, output,
                    args.Contains("--claude-session-start"), timeout.Token).GetAwaiter().GetResult();
            }
            catch (Exception) { /* A local collector must never interrupt Claude's task or open a window. */ }
            return 0;
        }
        // MCP owns no store and retains its original stdio. Its UI child passes here.
        if (!args.Contains("--demo"))
        {
            try
            {
                if (Codex.DesktopEnvironment.IsStorageRedirected(new Codex.TrackerServiceOptions().DataDirectory))
                {
                    if (args.Contains(Codex.DesktopEnvironment.RelaunchArgument))
                        throw new InvalidOperationException("Windows redirige encore le stockage. Lancez Codex Tracker depuis le menu Démarrer ; aucun compte n’a été réinitialisé.");
                    Codex.DesktopEnvironment.StartUnvirtualized(Environment.ProcessPath!,
                        args.Append(Codex.DesktopEnvironment.RelaunchArgument));
                    return 0;
                }
            }
            catch (Exception error)
            {
                System.Windows.MessageBox.Show("Impossible d’ouvrir le stockage habituel.\n\n" + error.Message,
                    "Codex Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
        var app = new App(); app.InitializeComponent(); return app.Run();
    }
}
