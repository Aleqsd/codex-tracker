using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodexTracker.App.Mcp;

internal static class McpHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            // WPF is never constructed in this process. stdout belongs exclusively to MCP.
            await using var client = new LocalClient(args);
            // Even an idle MCP host must be attached so app shutdown releases its executable for updates.
            await client.Start(CancellationToken.None);
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(client);
            builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<TrackerTools>(TrackerControl.Json);
            using var host = builder.Build();
            await host.RunAsync(client.Closed); return 0;
        }
        catch (OperationCanceledException) { return 0; }
        catch (Exception error) { await Console.Error.WriteLineAsync(args.Contains("--demo") ? error.ToString() : "Codex Tracker MCP indisponible. Reconnectez le client et vérifiez les réglages Assistants."); return 1; }
    }
}

[McpServerToolType]
internal sealed class TrackerTools(LocalClient client)
{
    private async Task<JsonElement> Call(string method, object args, CancellationToken token)
    {
        try { return await client.Call(method, args, token); }
        catch { return JsonSerializer.SerializeToElement(new { error = "tracker_unavailable", detail = "Le tracker ne répond pas. Reconnectez le client MCP ; démarrage limité à 20 secondes." }); }
    }
    [McpServerTool(Name = "get_status", ReadOnly = true), Description("État du tracker et révision à utiliser pour les modifications. MCP doit être activé dans les réglages.")]
    public Task<JsonElement> Status(CancellationToken token) => Call("status", new { }, token);
    [McpServerTool(Name = "list_accounts", ReadOnly = true), Description("Comptes et quotas observés, type d’abonnement, dates, réserves et ancienneté. Seul le compte actif est actualisé.")]
    public Task<JsonElement> Accounts(CancellationToken token) => Call("accounts", new { }, token);
    [McpServerTool(Name = "list_resets", ReadOnly = true), Description("Échéances par compte et type, dates connues ou nulles, date d’observation et données anciennes.")]
    public Task<JsonElement> Resets(CancellationToken token) => Call("resets", new { }, token);
    [McpServerTool(Name = "get_notification_history", ReadOnly = true), Description("Les 200 derniers rappels du journal local. Accepté ne signifie pas livré.")]
    public Task<JsonElement> History(CancellationToken token) => Call("history", new { }, token);
    [McpServerTool(Name = "get_preferences", ReadOnly = true)]
    public Task<JsonElement> Preferences(CancellationToken token) => Call("preferences", new { }, token);
    [McpServerTool(Name = "get_reminder_rules", ReadOnly = true)]
    public Task<JsonElement> Rules(CancellationToken token) => Call("rules", new { }, token);
    [McpServerTool(Name = "get_channels", ReadOnly = true), Description("État et destinataires des connecteurs. Ne retourne jamais les identifiants ou clés.")]
    public Task<JsonElement> Channels(CancellationToken token) => Call("channels", new { }, token);
    [McpServerTool(Name = "get_action_status", ReadOnly = true), Description("Consulter une demande : pending, completed, cancelled, expired ou unknown. Ne jamais répéter une action unknown avec un nouvel identifiant.")]
    public Task<JsonElement> Action(string requestId, CancellationToken token) => Call("action", new { requestId }, token);
    [McpServerTool(Name = "refresh_active_account", Destructive = false), Description("Actualise uniquement le compte actif, sans basculer ni renouveler les comptes inactifs.")]
    public Task<JsonElement> Refresh(CancellationToken token) => Call("refresh", new { }, token);
    [McpServerTool(Name = "open_page", Destructive = false), Description("Ouvre Comptes, Resets, Général, Rappels, Canaux, Historique, Calendrier, Assistants ou Application.")]
    public Task<JsonElement> Show(string page, CancellationToken token) => Call("show", new { page }, token);
    [McpServerTool(Name = "update_preferences", Destructive = false), Description("Remplace les réglages généraux après lecture. requestId UUID stable : réutilisez le même pour toute répétition. expectedRevision vient de la dernière lecture.")]
    public Task<JsonElement> SetPreferences(string requestId, string expectedRevision, General settings, CancellationToken token) => Change("set_preferences", requestId, expectedRevision, settings, token);
    [McpServerTool(Name = "update_phone_policy", Destructive = false), Description("Change les limites et heures silencieuses. Une augmentation des permissions nécessite une confirmation locale, valable cinq minutes.")]
    public Task<JsonElement> SetPhone(string requestId, string expectedRevision, PhonePolicy policy, CancellationToken token) => Change("set_phone", requestId, expectedRevision, policy, token);
    [McpServerTool(Name = "update_reminder_rules", Destructive = false), Description("Remplace toutes les règles. Délais en minutes : 30,60,360,720,1440,4320,10080 ; Short seulement 30/60. AccountIds null = tous. Nouveaux envois externes soumis à confirmation locale.")]
    public Task<JsonElement> SetRules(string requestId, string expectedRevision, ReminderRule[] rules, CancellationToken token) => Change("set_rules", requestId, expectedRevision, rules, token);
    [McpServerTool(Name = "configure_channel", Destructive = false), Description("Remplace twilio OU sendgrid. Clés en écriture seule : elles peuvent apparaître dans le contexte de votre assistant. Enabled=false par défaut. Aucun envoi lors de l’enregistrement. Configuration active soumise à confirmation locale.")]
    public Task<JsonElement> Configure(string requestId, string expectedRevision, ChannelChange configuration, CancellationToken token) => Change("set_channel", requestId, expectedRevision, configuration, token);
    [McpServerTool(Name = "delete_credentials", Destructive = true), Description("Supprime les identifiants et désactive le connecteur twilio ou sendgrid.")]
    public Task<JsonElement> Delete(string requestId, string expectedRevision, string provider, CancellationToken token) => Change("delete_credentials", requestId, expectedRevision, new { provider }, token);
    [McpServerTool(Name = "test_notification", Destructive = false), Description("Test Windows, Sms, Call ou Email. Externe = confirmation locale et frais éventuels ; respecte les limites existantes. Réutiliser le même requestId UUID pour éviter un doublon, même après une erreur réseau.")]
    public Task<JsonElement> Test(string requestId, string expectedRevision, ReminderChannel channel, CancellationToken token) => Change("test", requestId, expectedRevision, new { channel }, token);
    private Task<JsonElement> Change(string method, string requestId, string expectedRevision, object value, CancellationToken token) => Call(method, new { requestId, expectedRevision, value }, token);
}
