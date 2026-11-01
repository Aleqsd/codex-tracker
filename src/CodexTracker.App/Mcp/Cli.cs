using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexTracker.App.Mcp;

/// <summary>
/// Command line twin of the MCP tools: `CodexTracker.exe --cli list_resets`, same names, same arguments, JSON on stdout.
/// It uses the same local channel, so the Assistants switch, revisions and local confirmations apply unchanged.
/// </summary>
internal static class Cli
{
    private sealed record Command(string Method, string? Value, string Help);

    // Read commands pass their arguments as is; changes wrap the named argument (or the rest) in `value`.
    private static readonly Dictionary<string, Command> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["get_status"] = new("status", null, "État du tracker et révision."),
        ["list_accounts"] = new("accounts", null, "Comptes, quotas, réserves et ancienneté."),
        ["list_resets"] = new("resets", null, "Échéances par compte."),
        ["get_notification_history"] = new("history", null, "Derniers rappels du journal."),
        ["get_preferences"] = new("preferences", null, "Réglages généraux."),
        ["get_reminder_rules"] = new("rules", null, "Règles de rappel."),
        ["get_channels"] = new("channels", null, "Connecteurs, sans clés."),
        ["get_google_calendar"] = new("google", null, "Google Agenda et Gmail."),
        ["get_diagnostic"] = new("diagnostic", null, "Diagnostic de support et demande GitHub pré-remplie."),
        ["get_usage_history"] = new("usage_history", null, "accountId=<guid> hours=24|168 window=Weekly|Short"),
        ["get_global_reset_feed"] = new("global_feed", null, "Annonces de reset général et statut des comptes."),
        ["get_update_status"] = new("update_status", null, "Version, recherche et mise à jour prête."),
        ["get_claude_code_setup"] = new("claude_setup", null, "Réglage de barre de statut Claude Code."),
        ["get_whats_new"] = new("whats_new", null, "Nouveautés vues et à voir."),
        ["get_action_status"] = new("action", null, "requestId=<uuid>"),
        ["refresh_active_account"] = new("refresh", null, "Actualise le compte actif."),
        ["open_page"] = new("show", null, "page=Resets"),
        ["update_preferences"] = new("set_preferences", "settings", "settings=<json> (lire get_preferences d’abord)"),
        ["update_phone_policy"] = new("set_phone", "policy", "policy=<json>"),
        ["update_reminder_rules"] = new("set_rules", "rules", "rules=<json>"),
        ["configure_channel"] = new("set_channel", "configuration", "configuration=<json>"),
        ["delete_credentials"] = new("delete_credentials", "", "provider=twilio|sendgrid|ntfy"),
        ["test_notification"] = new("test", "", "channel=Windows|Sms|Call|Email|Push"),
        ["connect_google_calendar"] = new("google_connect", "", "mail=true|false"),
        ["disconnect_google_calendar"] = new("google_disconnect", "", ""),
        ["import_google_client"] = new("google_import", "", "path=<fichier.json>"),
        ["sync_google_calendar"] = new("google_sync", "", ""),
        ["add_reserve_to_calendar"] = new("google_add", "", "accountId=<guid> creditId=<id>"),
        ["set_tray_quota"] = new("set_tray_quota", "", "provider=Codex|ClaudeCode window=Weekly|Short"),
        ["set_start_with_windows"] = new("set_startup", "", "enabled=true|false"),
        ["declare_codex_reset"] = new("declare_reset", "", "at=<date ISO passée>|null"),
        ["set_reset_alert"] = new("set_reset_alert", "", "accountId=<guid> kind=Weekly|Short at=<date> enabled=true|false"),
        ["rename_account"] = new("rename_account", "", "accountId=<guid> name=<nom>"),
        ["remove_account"] = new("remove_account", "", "accountId=<guid> (confirmation dans le tracker)"),
        ["detect_current_account"] = new("detect_account", "", "Détecte le compte actif."),
        ["complete_onboarding"] = new("complete_onboarding", "", "Termine l’accueil."),
        ["dismiss_notice"] = new("dismiss_notice", "", "kind=manualReset|expectedResets|advice"),
        ["hide_pill"] = new("hide_pill", "", "key=<clé de get_status.pill>"),
        ["set_account_avatar"] = new("set_avatar", "", "accountId=<guid> path=<image.png|jpg>|null"),
        ["export_calendar_ics"] = new("export_ics", "", "[accountId=<guid>] [path=<nouveau fichier .ics>]"),
        ["prepare_google_calendar_import"] = new("prepare_google_import", "", "[accountId=<guid>]"),
        ["check_global_reset_announcements"] = new("check_announcements", "", "Relit les annonces publiques."),
        ["forget_google_client"] = new("forget_google_client", "", "(confirmation dans le tracker)"),
        ["check_for_update"] = new("check_update", "", "Recherche une mise à jour."),
        ["install_update"] = new("install_update", "", "(confirmation dans le tracker ; redémarre)"),
        ["quit_tracker"] = new("quit", "", "(confirmation dans le tracker)"),
        ["restart_tracker"] = new("restart", "", "(confirmation dans le tracker)"),
        ["acknowledge_recovery"] = new("acknowledge_recovery", "", "(confirmation dans le tracker)"),
        ["dismiss_whats_new"] = new("dismiss_whats_new", "", "Marque les nouveautés comme vues."),
        ["export_profile"] = new("export_profile", "", "path=<nouveau fichier> password=<8 caractères ou plus> (confirmation)"),
        ["import_profile"] = new("import_profile", "", "path=<fichier> password=<mot de passe> (confirmation)"),
    };

    internal static IReadOnlyCollection<string> Names => Commands.Keys;

    internal static async Task<int> RunAsync(string[] args)
    {
        using var output = Output();
        var start = Array.FindIndex(args, a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase)) + 1;
        var rest = new List<string>();
        for (var i = start; i < args.Length; i++)
        {
            if (args[i] == "--demo") continue;
            if (args[i] == "--demo-instance") { i++; continue; }
            rest.Add(args[i]);
        }
        if (rest.Count == 0 || rest[0] is "help" or "--help" or "-h" || !Commands.TryGetValue(rest[0], out var command))
        {
            output.WriteLine("Usage : CodexTracker.exe --cli <commande> [clé=valeur ...] [{json}] [@fichier.json]");
            output.WriteLine("Accès Assistants requis (Réglages → Assistants). Sortie JSON ; code 0 si succès, 1 si erreur, 2 si usage.");
            foreach (var (name, c) in Commands.OrderBy(c => c.Key)) output.WriteLine($"  {name,-28} {c.Help}");
            return rest.Count == 0 || rest[0] is "help" or "--help" or "-h" ? 0 : 2;
        }
        JsonObject arguments;
        try { arguments = Arguments(rest.Skip(1)); }
        catch (Exception error) when (error is JsonException or FormatException or IOException or UnauthorizedAccessException)
        { output.WriteLine(JsonSerializer.Serialize(new { error = "invalid_arguments", detail = error.Message })); return 2; }

        try
        {
            await using var client = new LocalClient(args);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await client.Start(timeout.Token);
            if (command.Value is not null)
            {
                // A change needs the latest revision and a stable request id; both may be given to retry safely.
                var requestId = Take(arguments, "requestId") ?? Guid.NewGuid().ToString();
                var revision = Take(arguments, "expectedRevision");
                if (revision is null)
                {
                    var status = await client.Call("status", new { }, timeout.Token);
                    if (!status.TryGetProperty("result", out var result)) return Print(output, status);
                    revision = result.GetProperty("revision").GetString();
                }
                JsonNode? value = command.Value.Length > 0 ? arguments[command.Value]?.DeepClone() : arguments;
                arguments = new JsonObject { ["requestId"] = requestId, ["expectedRevision"] = revision, ["value"] = value ?? new JsonObject() };
            }
            return Print(output, await client.Call(command.Method, arguments, timeout.Token));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException or UnauthorizedAccessException)
        {
            output.WriteLine(JsonSerializer.Serialize(new { error = "tracker_unavailable", detail = "Le tracker ne répond pas : ouvrez-le puis réessayez." }));
            return 1;
        }
    }

    private static int Print(TextWriter output, JsonElement response)
    {
        output.WriteLine(JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return response.TryGetProperty("error", out _) ? 1 : 0;
    }

    private static string? Take(JsonObject arguments, string name)
    {
        if (!arguments.TryGetPropertyValue(name, out var node)) return null;
        arguments.Remove(name); return node?.ToString();
    }

    /// <summary>`clé=valeur` pairs (true/false/numbers/JSON values parsed), a JSON object, or @file.json.</summary>
    internal static JsonObject Arguments(IEnumerable<string> parts)
    {
        var result = new JsonObject();
        foreach (var part in parts)
        {
            if (part.StartsWith('@')) Merge(result, File.ReadAllText(part[1..]));
            else if (part.TrimStart().StartsWith('{')) Merge(result, part);
            else
            {
                var equals = part.IndexOf('=');
                if (equals <= 0) throw new FormatException($"Argument attendu sous la forme clé=valeur : {part}");
                result[part[..equals]] = Value(part[(equals + 1)..]);
            }
        }
        return result;
    }

    private static void Merge(JsonObject target, string json)
    {
        var source = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("Objet JSON attendu.");
        foreach (var (key, value) in source.ToArray()) { source.Remove(key); target[key] = value; }
    }

    private static JsonNode? Value(string text)
    {
        var trimmed = text.Trim();
        if (trimmed is "true" or "false" or "null" || trimmed.StartsWith('{') || trimmed.StartsWith('[') ||
            double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) && (!trimmed.StartsWith('0') || trimmed == "0" || trimmed.StartsWith("0.")))
        {
            try { return JsonNode.Parse(trimmed); } catch (JsonException) { }
        }
        return JsonValue.Create(text);
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    /// <summary>The executable is a WinExe: attach to the calling terminal when stdout is not already redirected.</summary>
    private static StreamWriter Output()
    {
        if (!Console.IsOutputRedirected) AttachConsole(-1);
        return new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    }
}
