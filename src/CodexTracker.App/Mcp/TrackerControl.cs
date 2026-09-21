using System.Text.Json;
using CodexTracker.Codex;

namespace CodexTracker.App.Mcp;

internal sealed class TrackerControl : IDisposable
{
    private readonly MainWindow _window;
    private readonly PreferencesStore _preferences;
    private readonly ApplicationCommands _commands;
    private readonly AssistantActions _actions;
    private readonly Dictionary<string, (string Revision, Func<Task<string>> Run)> _pending = new();
    private readonly HashSet<string> _executed = new();
    private readonly Dictionary<string, TrackerDialog> _dialogs = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _accountIds = "";
    private bool _disposed;
    internal int Connections { get; set; }
    internal static readonly JsonSerializerOptions Json = new(NotificationFiles.Json) { WriteIndented = false, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    internal TrackerControl(MainWindow window, bool demo)
    {
        _window = window; _preferences = window.Preferences; _commands = window.Commands;
        _actions = new(demo ? null : _preferences.DataDirectory);
        _accountIds = string.Join(",", _window.TrackerService.State.Accounts.Select(a => a.Profile.Id).Order());
        _preferences.Changed += Changed;
        _timer.Tick += (_, _) => Expire(); _timer.Start();
    }
    private void Changed(object? sender, EventArgs e)
    {
        foreach (var id in _pending.Keys.ToArray())
            if (!_preferences.Current.McpEnabled || _pending[id].Revision != Revision) Cancel(id, "Réglages modifiés ; relisez l’état.");
    }
    private string Revision
    {
        get
        {
            var ids = string.Join(",", _window.TrackerService.State.Accounts.Select(a => a.Profile.Id).Order());
            if (ids != _accountIds) { _accountIds = ids; _preferences.Touch(); }
            return _commands.Revision;
        }
    }
    internal async Task<object> HandleAsync(string method, JsonElement args)
    {
        if (_disposed) throw new InvalidOperationException("tracker_stopping");
        if (_window.AssistantError is not null) throw new InvalidOperationException("assistant_unavailable");
        if (!_preferences.Current.McpEnabled) throw new InvalidOperationException("mcp_disabled: activez Assistants dans les réglages du tracker.");
        var revision = Revision;
        var state = _window.TrackerService.State;
        switch (method)
        {
            case "status": return new { revision, version = typeof(App).Assembly.GetName().Version?.ToString(), activeAccountId = state.ActiveAccount?.Profile.Id, state.IsBusy, connections = Connections, reminderError = _window.Reminders.Error };
            case "accounts": return new { revision, accounts = state.Accounts.Select(a => new { id = a.Profile.Id, email = a.Profile.Email, a.IsActiveInCodex, a.IsConnected, a.IsStale, error = a.Error is null ? null : "Actualisation indisponible", snapshot = a.Snapshot }) };
            case "resets": return new { revision, resets = ResetSchedule.Entries(state).Select(e => new { accountId = e.Account.Profile.Id, account = e.Account.Profile.Email, e.Kind, e.At, e.CreditId, e.CreditTitle, e.GrantedAt, e.IsUndetailedReserve, observedAt = e.Account.Snapshot?.FetchedAt, e.Account.IsStale }) };
            case "history": return new { revision, entries = _window.Reminders.History.OrderByDescending(h => h.UpdatedAt).Take(200).ToArray() };
            case "preferences": return new { revision, settings = General.From(_preferences.Current), phone = _preferences.Current.PhonePolicy };
            case "rules": return new { revision, rules = _preferences.Current.ReminderRules };
            case "channels":
                var s = _window.Reminders.Secrets.Read();
                return new { revision, twilio = new { s.Twilio.Enabled, hasCredentials = s.Twilio.Secret.Length > 0, s.Twilio.SmsFrom, s.Twilio.CallFrom, s.Twilio.To }, sendGrid = new { s.SendGrid.Enabled, hasCredentials = s.SendGrid.ApiKey.Length > 0, s.SendGrid.From, s.SendGrid.To } };
            case "action": return Public(_actions.Find(Read<ActionQuery>(args).RequestId) ?? throw new ArgumentException("Action inconnue."));
            case "refresh":
                await _window.TrackerService.RefreshAsync(); return new { refreshed = true, revision = Revision, observedAt = _window.TrackerService.State.ActiveAccount?.Snapshot?.FetchedAt };
            case "show": _window.OpenPage(Read<PageQuery>(args).Page); return new { opened = true };
        }
        var request = Read<ChangeRequest>(args);
        request = request with { RequestId = Guid.ParseExact(request.RequestId, "D").ToString("D") };
        var fingerprint = AssistantActions.Fingerprint(method, JsonSerializer.Serialize(request, Json));
        if (_actions.Find(request.RequestId) is { } previous)
        {
            if (previous.Fingerprint != fingerprint) throw new InvalidOperationException("request_id_conflict");
            return Public(previous);
        }
        _commands.CheckRevision(request.ExpectedRevision);
        Func<Task<string>> run;
        var confirm = false;
        var summary = "";
        switch (method)
        {
            case "set_preferences":
                var p = Read<General>(request.Value);
                var candidate = p.Apply(_preferences.Current);
                ApplicationCommands.ValidatePhone(candidate.PhonePolicy);
                run = () => { _commands.SavePreferences(p.Apply); return Task.FromResult("Réglages enregistrés."); };
                summary = "Modifier les réglages généraux."; break;
            case "set_phone":
                var phone = Read<PhonePolicy>(request.Value); ApplicationCommands.ValidatePhone(phone);
                confirm = ApplicationCommands.ExpandsPhone(_preferences.Current.PhonePolicy, phone);
                summary = $"Limites : {phone.SmsPerDay} SMS et {phone.CallsPerDay} appels / jour.\nSilence : {(phone.QuietEnabled ? $"{phone.QuietStart}:00–{phone.QuietEnd}:00" : "désactivé")} ({phone.TimeZoneId}).";
                run = () => { _commands.SavePreferences(p => p with { PhonePolicy = phone }); return Task.FromResult("Limites enregistrées."); }; break;
            case "set_rules":
                var rules = Read<ReminderRule[]>(request.Value); ApplicationCommands.ValidateRules(rules);
                if (rules.Any(r => r.AccountIds?.Any(id => !state.Accounts.Any(a => a.Profile.Id == id)) == true)) throw new ArgumentException("Compte inconnu.");
                confirm = ApplicationCommands.ExpandsExternalRules(_preferences.Current.ReminderRules!, rules);
                summary = "Remplacer les règles de rappel :\n" + string.Join("\n", rules.Where(r => r.Enabled && r.Channels.Length > 0).Select(r =>
                    $"{ReminderPlanner.Label(r.Kind)} · {string.Join(", ", r.LeadMinutes)} min avant · {string.Join(", ", r.Channels)} · " + (r.AccountIds is null ? "tous les comptes" : string.Join(", ", state.Accounts.Where(a => r.AccountIds.Contains(a.Profile.Id)).Select(a => a.Profile.Email))))) + Destinations();
                run = () => { _commands.SavePreferences(p => p with { ReminderRules = rules }); return Task.FromResult("Règles enregistrées."); }; break;
            case "set_channel":
                var change = Read<ChannelChange>(request.Value);
                var old = _window.Reminders.Secrets.Read(); var next = old;
                if (change.Provider == "twilio" && change.Twilio is not null && change.SendGrid is null) next = old with { Twilio = change.Twilio };
                else if (change.Provider == "sendgrid" && change.SendGrid is not null && change.Twilio is null) next = old with { SendGrid = change.SendGrid };
                else throw new ArgumentException("Fournir un seul connecteur : twilio ou sendgrid.");
                // A complete replacement is explicit: omitted Enabled remains false, so saving keys never enables delivery.
                confirm = next.Twilio.Enabled && next.Twilio != old.Twilio || next.SendGrid.Enabled && next.SendGrid != old.SendGrid;
                summary = change.Provider == "twilio" ? $"Twilio · {(next.Twilio.Enabled ? "activer / modifier" : "désactiver")}\nSMS depuis {next.Twilio.SmsFrom}\nAppels depuis {next.Twilio.CallFrom}\nDestinataire : {next.Twilio.To}\nIdentifiants : masqués." : $"SendGrid · {(next.SendGrid.Enabled ? "activer / modifier" : "désactiver")}\nDe : {next.SendGrid.From}\nVers : {next.SendGrid.To}\nClé : masquée.";
                run = () => { _commands.SaveSecrets(next); return Task.FromResult("Connecteur enregistré ; aucun envoi effectué."); }; break;
            case "delete_credentials":
                var provider = Read<ProviderQuery>(request.Value).Provider;
                if (provider is not ("twilio" or "sendgrid")) throw new ArgumentException("Connecteur inconnu.");
                run = () => { var secrets = _window.Reminders.Secrets.Read(); _commands.SaveSecrets(provider == "twilio" ? secrets with { Twilio = new() } : secrets with { SendGrid = new() }); return Task.FromResult("Identifiants supprimés ; canal désactivé."); };
                summary = "Supprimer les identifiants " + provider; break;
            case "test":
                var channel = Read<TestQuery>(request.Value).Channel;
                if (!Enum.IsDefined(channel)) throw new ArgumentException("Canal inconnu.");
                confirm = channel != ReminderChannel.Windows;
                summary = $"Envoyer un test {channel}. Les frais éventuels sont facturés par votre prestataire." + Destinations() + "\nMessage : test de rappel Codex Tracker. Les limites et heures silencieuses restent appliquées.";
                run = async () => { var result = await _window.Reminders.TestAsync(channel, () => !_disposed && _preferences.Current.McpEnabled && _commands.Revision == revision); return $"{result.Status} · {result.Detail}"; }; break;
            default: throw new ArgumentException("Commande inconnue.");
        }
        var item = _actions.Begin(request.RequestId, fingerprint, confirm, confirm ? "Confirmation requise dans Codex Tracker." : "Exécution en cours.");
        if (confirm)
        {
            if (_pending.Count >= 10) return Public(_actions.Set(item.Id, "cancelled", "Trop de confirmations en attente."));
            _pending[item.Id] = (revision, run);
            _ = _window.Dispatcher.BeginInvoke(() => Confirm(item.Id, summary));
            return Public(item);
        }
        return Public(await Execute(item.Id, run));
    }
    private string Destinations()
    {
        var s = _window.Reminders.Secrets.Read();
        return $"\nDestinataire SMS/appels : {s.Twilio.To}\nDestinataire email : {s.SendGrid.To}";
    }
    private async Task<AssistantAction> Execute(string id, Func<Task<string>> run)
    {
        if (!_executed.Add(id)) return _actions.Find(id)!;
        _pending.Remove(id);
        try { _actions.Set(id, "executing", "Exécution en cours."); return _actions.Set(id, "completed", await run()); }
        catch { return _actions.Set(id, "unknown", "Action non confirmée ou interrompue. Vérifiez l’état ; aucune répétition automatique."); }
    }
    private async void Confirm(string id, string summary)
    {
        if (!_pending.TryGetValue(id, out var pending) || _actions.Find(id)?.Status != "pending") return;
        _window.ShowPanel();
        var dialog = new TrackerDialog(_window, "Demande de l’assistant", summary + "\n\nExpire après cinq minutes. Aucun envoi avant votre confirmation.", "Confirmer", "Annuler");
        _dialogs[id] = dialog;
        var accepted = dialog.ShowDialog() == true; _dialogs.Remove(id);
        if (!_pending.ContainsKey(id)) return;
        if (!accepted) { Cancel(id, "Refusé ou fenêtre fermée."); return; }
        if (_actions.Find(id)?.Status != "pending" || pending.Revision != Revision || !_preferences.Current.McpEnabled) { Cancel(id, "Confirmation expirée ou réglages modifiés."); return; }
        try { await Execute(id, pending.Run); }
        catch { _window.AssistantError = "MCP suspendu : impossible de persister le résultat. Aucun nouvel envoi autorisé."; }
    }
    private void Cancel(string id, string reason)
    {
        _pending.Remove(id); if (_actions.Find(id)?.Status == "pending") _actions.Set(id, "cancelled", reason);
        if (_dialogs.Remove(id, out var dialog)) dialog.Close();
    }
    private void Expire()
    {
        try
        {
            _ = Revision;
            foreach (var id in _pending.Keys.ToArray()) if (_actions.Find(id)?.Status != "pending") Cancel(id, "Confirmation expirée.");
        }
        catch { _window.AssistantError = "MCP suspendu : journal local indisponible."; _timer.Stop(); _pending.Clear(); foreach (var dialog in _dialogs.Values.ToArray()) dialog.Close(); }
    }
    private static object Public(AssistantAction a) => new { requestId = a.Id, a.Status, a.ExpiresAt, a.Detail };
    private static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new ArgumentException("Paramètres manquants.");
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _preferences.Changed -= Changed;
        foreach (var id in _pending.Keys.ToArray()) Cancel(id, "Application fermée.");
    }
}

internal sealed record ChangeRequest(string RequestId, string ExpectedRevision, JsonElement Value);
internal sealed record ActionQuery(string RequestId);
internal sealed record PageQuery(string Page);
internal sealed record ProviderQuery(string Provider);
internal sealed record TestQuery(ReminderChannel Channel);
internal sealed record ChannelChange(string Provider, TwilioSettings? Twilio = null, SendGridSettings? SendGrid = null);
internal sealed record General(ThemeMode ThemeMode, SortMode SortMode, bool Alert20, bool Alert10, bool Alert5, bool ResetNotifications, bool HoverPreview, int RefreshMinutes, bool AdaptiveRefresh)
{
    internal static General From(TrackerPreferences p) => new(p.ThemeMode, p.SortMode, p.Alert20, p.Alert10, p.Alert5, p.ResetNotifications, p.HoverPreview, p.RefreshMinutes, p.AdaptiveRefresh);
    internal TrackerPreferences Apply(TrackerPreferences p) => p with { ThemeMode = ThemeMode, SortMode = SortMode, Alert20 = Alert20, Alert10 = Alert10, Alert5 = Alert5, ResetNotifications = ResetNotifications, HoverPreview = HoverPreview, RefreshMinutes = RefreshMinutes, AdaptiveRefresh = AdaptiveRefresh };
}
