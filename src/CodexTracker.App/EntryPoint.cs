namespace CodexTracker.App;

internal static class EntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--mcp")) return Mcp.McpHost.RunAsync(args).GetAwaiter().GetResult();
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
