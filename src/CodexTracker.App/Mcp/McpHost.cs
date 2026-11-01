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
        var connected = false;
        try
        {
            // WPF is never constructed in this process. stdout belongs exclusively to MCP.
            await using var client = new LocalClient(args);
            // Even an idle MCP host must be attached so app shutdown releases its executable for updates.
            await client.Start(CancellationToken.None);
            connected = true;
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(client);
            builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<TrackerTools>(TrackerControl.Json);
            using var host = builder.Build();
            await host.RunAsync(client.Closed); return 0;
        }
        catch (OperationCanceledException) when (connected) { return 0; }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Codex Tracker MCP : démarrage impossible après 20 secondes. Ouvrez le tracker, puis reconnectez le client.");
            return 1;
        }
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
    [McpServerTool(Name = "list_resets", ReadOnly = true), Description("Échéances par compte et type, dates connues, estimées après un reset Codex déclaré (isProjected) ou nulles, date d’observation et données anciennes.")]
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
    [McpServerTool(Name = "update_preferences", Destructive = false), Description("Remplace les réglages généraux après lecture (langue, thème, alertes ; sortMode est réservé et sans effet ; pastille, copie téléphone, icônes, mises à jour…) ; les champs facultatifs omis gardent leur valeur. requestId UUID stable : réutilisez le même pour toute répétition. expectedRevision vient de la dernière lecture.")]
    public Task<JsonElement> SetPreferences(string requestId, string expectedRevision, General settings, CancellationToken token) => Change("set_preferences", requestId, expectedRevision, settings, token);
    [McpServerTool(Name = "update_phone_policy", Destructive = false), Description("Change les limites et heures silencieuses. Une augmentation des permissions nécessite une confirmation locale, valable cinq minutes.")]
    public Task<JsonElement> SetPhone(string requestId, string expectedRevision, PhonePolicy policy, CancellationToken token) => Change("set_phone", requestId, expectedRevision, policy, token);
    [McpServerTool(Name = "update_reminder_rules", Destructive = false), Description("Remplace toutes les règles. Délais en minutes : 30,60,360,720,1440,4320,10080 ; Short seulement 30/60. AccountIds null = tous. Nouveaux envois externes soumis à confirmation locale.")]
    public Task<JsonElement> SetRules(string requestId, string expectedRevision, ReminderRule[] rules, CancellationToken token) => Change("set_rules", requestId, expectedRevision, rules, token);
    [McpServerTool(Name = "configure_channel", Destructive = false), Description("Remplace twilio, sendgrid OU ntfy (notifications sur le téléphone : server https, topic secret de 12 à 64 caractères, token facultatif). Clés et sujet en écriture seule : elles peuvent apparaître dans le contexte de votre assistant. Enabled=false par défaut. Aucun envoi lors de l’enregistrement. Configuration active soumise à confirmation locale.")]
    public Task<JsonElement> Configure(string requestId, string expectedRevision, ChannelChange configuration, CancellationToken token) => Change("set_channel", requestId, expectedRevision, configuration, token);
    [McpServerTool(Name = "delete_credentials", Destructive = true), Description("Supprime les identifiants et désactive le connecteur twilio, sendgrid ou ntfy.")]
    public Task<JsonElement> Delete(string requestId, string expectedRevision, string provider, CancellationToken token) => Change("delete_credentials", requestId, expectedRevision, new { provider }, token);
    [McpServerTool(Name = "test_notification", Destructive = false), Description("Test Windows, Sms, Call, Email ou Push (téléphone via ntfy, sans confirmation). Email part par Gmail (à l’utilisateur lui-même, sans confirmation) quand l’envoi Gmail est accordé et SendGrid non configuré. Sms, appels et SendGrid = confirmation locale et frais éventuels. Réutiliser le même requestId UUID pour éviter un doublon, même après une erreur réseau.")]
    public Task<JsonElement> Test(string requestId, string expectedRevision, ReminderChannel channel, CancellationToken token) => Change("test", requestId, expectedRevision, new { channel }, token);
    [McpServerTool(Name = "get_google_calendar", ReadOnly = true), Description("Connexion Google Agenda et e-mails Gmail : état, adresse, problème éventuel, resets en réserve et statut de leur événement (lien Google). Ne retourne jamais de jeton.")]
    public Task<JsonElement> Google(CancellationToken token) => Call("google", new { }, token);
    [McpServerTool(Name = "connect_google_calendar", Destructive = false), Description("Ouvre l’autorisation Google dans le navigateur de l’utilisateur (agenda, et envoi d’e-mails Gmail si mail=true). L’utilisateur valide dans Google ; suivre avec get_google_calendar.")]
    public Task<JsonElement> ConnectGoogle(string requestId, string expectedRevision, bool mail, CancellationToken token) => Change("google_connect", requestId, expectedRevision, new { mail }, token);
    [McpServerTool(Name = "disconnect_google_calendar", Destructive = true), Description("Révoque l’accès Google du tracker. Les événements déjà créés restent dans Google Agenda.")]
    public Task<JsonElement> DisconnectGoogle(string requestId, string expectedRevision, CancellationToken token) => Change("google_disconnect", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "import_google_client", Destructive = false), Description("Importe le fichier JSON d’un client OAuth Google « Application de bureau » (chemin local), à la place du client intégré. Ne connecte pas : appeler ensuite connect_google_calendar.")]
    public Task<JsonElement> ImportGoogle(string requestId, string expectedRevision, string path, CancellationToken token) => Change("google_import", requestId, expectedRevision, new { path }, token);
    [McpServerTool(Name = "sync_google_calendar", Destructive = false), Description("Synchronise maintenant : ajoute les resets en réserve manquants (si l’ajout automatique est actif), retire ceux utilisés, vérifie les événements.")]
    public Task<JsonElement> SyncGoogle(string requestId, string expectedRevision, CancellationToken token) => Change("google_sync", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "add_reserve_to_calendar", Destructive = false), Description("Ajoute à Google Agenda l’expiration d’un reset en réserve (accountId et creditId lus dans list_resets). Idempotent : un événement existant est réutilisé.")]
    public Task<JsonElement> AddReserve(string requestId, string expectedRevision, Guid accountId, string creditId, CancellationToken token) => Change("google_add", requestId, expectedRevision, new { accountId, creditId }, token);
    [McpServerTool(Name = "get_diagnostic", ReadOnly = true), Description("Diagnostic de support sans compte, quota, chemin personnel ni secret, et l’adresse d’une demande GitHub pré-remplie (reportUrl) à faire relire à l’utilisateur avant envoi.")]
    public Task<JsonElement> Diagnostic(CancellationToken token) => Call("diagnostic", new { }, token);
    [McpServerTool(Name = "get_usage_history", ReadOnly = true), Description("Courbe du quota restant d’un compte : hours 24 ou 168, window Weekly ou Short (400 points au plus).")]
    public Task<JsonElement> UsageHistory(Guid accountId, int hours, ResetKind window, CancellationToken token) => Call("usage_history", new { accountId, hours, window }, token);
    [McpServerTool(Name = "get_global_reset_feed", ReadOnly = true), Description("Annonces publiques de reset général Codex, état du contrôle des sources et statut de chaque compte.")]
    public Task<JsonElement> GlobalFeed(CancellationToken token) => Call("global_feed", new { }, token);
    [McpServerTool(Name = "get_update_status", ReadOnly = true), Description("Version installée, dernière recherche, version prête, édition Store, page des versions.")]
    public Task<JsonElement> UpdateStatus(CancellationToken token) => Call("update_status", new { }, token);
    [McpServerTool(Name = "get_claude_code_setup", ReadOnly = true), Description("Réglage de barre de statut Claude Code à copier, fichier de configuration et état des relevés du terminal.")]
    public Task<JsonElement> ClaudeSetup(CancellationToken token) => Call("claude_setup", new { }, token);
    [McpServerTool(Name = "get_whats_new", ReadOnly = true), Description("Nouveautés : dernière version vue, version installée, nouveautés non vues (unseen) et historique (all) avec la page qui les montre.")]
    public Task<JsonElement> WhatsNewRead(CancellationToken token) => Call("whats_new", new { }, token);
    [McpServerTool(Name = "detect_current_account", Destructive = false), Description("Détecte maintenant le compte actif de Codex et Claude Code (bouton « Détecter maintenant »).")]
    public Task<JsonElement> Detect(string requestId, string expectedRevision, CancellationToken token) => Change("detect_account", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "complete_onboarding", Destructive = false), Description("Termine l’accueil du premier lancement (bouton « Compris »).")]
    public Task<JsonElement> Onboarding(string requestId, string expectedRevision, CancellationToken token) => Change("complete_onboarding", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "dismiss_notice", Destructive = false), Description("Masque un encart du tableau de bord jusqu’au prochain événement : kind manualReset, expectedResets ou advice (lus dans get_status.notices).")]
    public Task<JsonElement> DismissNotice(string requestId, string expectedRevision, string kind, CancellationToken token) => Change("dismiss_notice", requestId, expectedRevision, new { kind }, token);
    [McpServerTool(Name = "hide_pill", Destructive = false), Description("Masque la pastille à l’écran pour une situation (key lue dans get_status.pill) jusqu’à la prochaine alerte.")]
    public Task<JsonElement> HidePill(string requestId, string expectedRevision, string key, CancellationToken token) => Change("hide_pill", requestId, expectedRevision, new { key }, token);
    [McpServerTool(Name = "set_account_avatar", Destructive = false), Description("Photo d’un compte depuis une image locale PNG ou JPEG (8 Mo au plus, recadrée en carré) ; path null rétablit les initiales.")]
    public Task<JsonElement> Avatar(string requestId, string expectedRevision, Guid accountId, string? path = null, CancellationToken token = default) => Change("set_avatar", requestId, expectedRevision, new { accountId, path }, token);
    [McpServerTool(Name = "export_calendar_ics", Destructive = false), Description("Échéances au format iCalendar (.ics), pour un compte ou tous : sans path, renvoie le texte ; avec un chemin absolu nouveau, écrit le fichier (jamais de remplacement).")]
    public Task<JsonElement> ExportIcs(string requestId, string expectedRevision, Guid? accountId = null, string? path = null, CancellationToken token = default) => Change("export_ics", requestId, expectedRevision, new { accountId, path }, token);
    [McpServerTool(Name = "prepare_google_calendar_import", Destructive = false), Description("Prépare un fichier .ics dans le dossier du tracker et renvoie son chemin et la page d’import de Google Agenda.")]
    public Task<JsonElement> PrepareImport(string requestId, string expectedRevision, Guid? accountId = null, CancellationToken token = default) => Change("prepare_google_import", requestId, expectedRevision, new { accountId }, token);
    [McpServerTool(Name = "check_global_reset_announcements", Destructive = false), Description("Relit maintenant les annonces publiques de reset général (une fois par minute au plus) et renvoie leur état.")]
    public Task<JsonElement> CheckAnnouncements(string requestId, string expectedRevision, CancellationToken token) => Change("check_announcements", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "forget_google_client", Destructive = true), Description("Supprime le client OAuth Google importé (le client intégré reprend la main), après confirmation dans le tracker.")]
    public Task<JsonElement> ForgetClient(string requestId, string expectedRevision, CancellationToken token) => Change("forget_google_client", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "check_for_update", Destructive = false), Description("Recherche une mise à jour sur GitHub et la télécharge si le téléchargement automatique est actif ; renvoie get_update_status.")]
    public Task<JsonElement> CheckUpdate(string requestId, string expectedRevision, CancellationToken token) => Change("check_update", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "install_update", Destructive = true), Description("Installe la mise à jour prête et redémarre le tracker, après confirmation dans le tracker. La connexion MCP se coupe : reconnecter ensuite.")]
    public Task<JsonElement> InstallUpdate(string requestId, string expectedRevision, CancellationToken token) => Change("install_update", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "quit_tracker", Destructive = true), Description("Ferme Codex Tracker après confirmation dans le tracker : le suivi et les rappels s’arrêtent.")]
    public Task<JsonElement> Quit(string requestId, string expectedRevision, CancellationToken token) => Change("quit", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "restart_tracker", Destructive = false), Description("Redémarre Codex Tracker après confirmation (par exemple pour appliquer la langue) ; la connexion MCP se coupe.")]
    public Task<JsonElement> Restart(string requestId, string expectedRevision, CancellationToken token) => Change("restart", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "acknowledge_recovery", Destructive = false), Description("Valide les réglages récupérés après un fichier illisible, après confirmation dans le tracker (get_status.recoveryPending).")]
    public Task<JsonElement> Recovery(string requestId, string expectedRevision, CancellationToken token) => Change("acknowledge_recovery", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "dismiss_whats_new", Destructive = false), Description("Marque les nouveautés de la version installée comme vues (masque le bandeau).")]
    public Task<JsonElement> DismissWhatsNew(string requestId, string expectedRevision, CancellationToken token) => Change("dismiss_whats_new", requestId, expectedRevision, new { }, token);
    [McpServerTool(Name = "export_profile", Destructive = false), Description("Exporte le profil chiffré (comptes, historique, réglages) vers un chemin absolu nouveau, après confirmation dans le tracker. Le mot de passe (8 caractères au moins) passe par le contexte de l’assistant.")]
    public Task<JsonElement> ExportProfile(string requestId, string expectedRevision, string path, string password, CancellationToken token) => Change("export_profile", requestId, expectedRevision, new { path, password }, token);
    [McpServerTool(Name = "import_profile", Destructive = true), Description("Importe un profil chiffré après confirmation dans le tracker : ses réglages, rappels, noms et photos remplacent ceux de ce PC. Le mot de passe passe par le contexte de l’assistant.")]
    public Task<JsonElement> ImportProfile(string requestId, string expectedRevision, string path, string password, CancellationToken token) => Change("import_profile", requestId, expectedRevision, new { path, password }, token);
    [McpServerTool(Name = "set_tray_quota", Destructive = false), Description("Choisit le quota dessiné dans l’icône pour un outil : provider Codex ou ClaudeCode, window Weekly ou Short.")]
    public Task<JsonElement> TrayQuota(string requestId, string expectedRevision, AccountProvider provider, ResetKind window, CancellationToken token) => Change("set_tray_quota", requestId, expectedRevision, new { provider, window }, token);
    [McpServerTool(Name = "set_start_with_windows", Destructive = false), Description("Active ou coupe le démarrage du tracker avec Windows (lu dans get_preferences.startWithWindows).")]
    public Task<JsonElement> Startup(string requestId, string expectedRevision, bool enabled, CancellationToken token) => Change("set_startup", requestId, expectedRevision, new { enabled }, token);
    [McpServerTool(Name = "declare_codex_reset", Destructive = false), Description("Déclare un reset général Codex à une date passée (ISO 8601), affiché « 100 % · déclaré » jusqu’au prochain relevé réel ; at null annule la déclaration.")]
    public Task<JsonElement> DeclareReset(string requestId, string expectedRevision, DateTimeOffset? at = null, CancellationToken token = default) => Change("declare_reset", requestId, expectedRevision, new { at }, token);
    [McpServerTool(Name = "set_reset_alert", Destructive = false), Description("Active ou retire la notification ponctuelle (cloche) d’une échéance lue dans list_resets : accountId, kind Weekly ou Short, at exact, enabled.")]
    public Task<JsonElement> ResetAlert(string requestId, string expectedRevision, Guid accountId, ResetKind kind, DateTimeOffset at, bool enabled, CancellationToken token) => Change("set_reset_alert", requestId, expectedRevision, new { accountId, kind, at, enabled }, token);
    [McpServerTool(Name = "rename_account", Destructive = false), Description("Donne un nom d’affichage à un compte (60 caractères au plus) ; name vide ou null rétablit l’adresse.")]
    public Task<JsonElement> Rename(string requestId, string expectedRevision, Guid accountId, string? name = null, CancellationToken token = default) => Change("rename_account", requestId, expectedRevision, new { accountId, name }, token);
    [McpServerTool(Name = "remove_account", Destructive = true), Description("Retire un compte inactif et son historique local du tracker, après confirmation dans le tracker. Ne touche jamais au compte Codex ou Claude.")]
    public Task<JsonElement> Remove(string requestId, string expectedRevision, Guid accountId, CancellationToken token) => Change("remove_account", requestId, expectedRevision, new { accountId }, token);
    private Task<JsonElement> Change(string method, string requestId, string expectedRevision, object value, CancellationToken token) => Call(method, new { requestId, expectedRevision, value }, token);
}
