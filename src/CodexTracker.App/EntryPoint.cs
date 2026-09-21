namespace CodexTracker.App;

internal static class EntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--mcp")) return Mcp.McpHost.RunAsync(args).GetAwaiter().GetResult();
        var app = new App(); app.InitializeComponent(); return app.Run();
    }
}
