using System.Diagnostics;
using System.Text.Json;
using CodexTracker.App.Updates;
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
    private readonly bool _demo;
    private readonly UpdateService _updates;
    internal int Connections { get; set; }
    internal static readonly JsonSerializerOptions Json = new(NotificationFiles.Json) { WriteIndented = false, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(), UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    internal TrackerControl(MainWindow window, bool demo, UpdateService? updates = null)
    {
        _window = window; _preferences = window.Preferences; _commands = window.Commands; _demo = demo; _updates = updates ?? new UpdateService();
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
        var state = _window.TrackerService.State with { ManualCodexReset = _preferences.Current.ManualCodexReset };
        switch (method)
        {
            case "status": return new { revision, version = typeof(App).Assembly.GetName().Version?.ToString(), activeAccountId = state.ActiveAccount?.Profile.Id, activeAccountIds = state.ActiveAccounts.Select(a => a.Profile.Id),
                suggestions = state.ActiveAccounts.Select(a => (Active: a, Best: AccountSuggestions.For(state, a, DateTimeOffset.UtcNow))).Where(p => p.Best is not null)
                    .Select(p => new { provider = p.Active.Profile.Provider.ToString(), fromAccountId = p.Active.Profile.Id, toAccountId = p.Best!.Account.Profile.Id, toAccount = p.Best.Account.Profile.Email, remaining = p.Best.Remaining, estimated = p.Best.Estimated }),
                pill = _preferences.Current.ScreenPill ? ScreenPill.Current(state with { ManualCodexReset = _preferences.Current.ManualCodexReset }, DateTimeOffset.UtcNow, _preferences.Current.PillHidden)
                    .Select(p => new { p.Key, kind = p.Kind.ToString(), accountId = p.Account.Profile.Id, window = p.Window.ToString(), p.Remaining, p.At }) : null,
                manualCodexReset = state.ManualCodexReset, state.IsBusy, refresh = RefreshReport(_window.TrackerService, revision), connections = Connections, reminderError = _window.Reminders.Error,
                onboardingComplete = state.OnboardingComplete, notices = AgentQueries.Notices(state, _preferences.Current, DateTimeOffset.UtcNow),
                healthWarnings = _window.HealthWarnings(), recoveryPending = _preferences.RecoveryRequired,
                windowsNotifications = WindowsNotificationDelivery.ReadState().ToString() };
            case "accounts": return new { revision, accounts = state.Accounts.Select(a => new { id = a.Profile.Id, email = a.Profile.Email, provider = a.Profile.Provider.ToString(), organization = a.Profile.OrganizationName, a.IsActive, a.IsActiveInCodex, a.IsActiveInClaudeCode, a.IsConnected, a.IsStale, error = a.Error is null ? null : "Actualisation indisponible", manualReset = state.ManualCodexReset?.Applies(a, ResetKind.Weekly, DateTimeOffset.UtcNow) == true, displayedWeeklyRemaining = QuotaPresentation.Remaining(state, a, ResetKind.Weekly, DateTimeOffset.UtcNow),
                // False when the plan has no 5-hour quota (Codex Pro, a reading with the week only): the interface hides it.
                hasShortWindow = a.HasShortWindow, displayedShortRemaining = a.HasShortWindow ? QuotaPresentation.Remaining(state, a, ResetKind.Short, DateTimeOffset.UtcNow) : null,
                forecast = AgentQueries.Forecast(_window.TrackerService, a, DateTimeOffset.UtcNow), pace = AgentQueries.Pace(state, a, DateTimeOffset.UtcNow), snapshot = a.Snapshot }) };
            case "resets": return new { revision, resets = ResetSchedule.Entries(state).Select(e => new { accountId = e.Account.Profile.Id, account = e.Account.Profile.Email, provider = e.Account.Profile.Provider.ToString(), e.Kind, e.At, e.IsProjected, e.CreditId, e.CreditTitle, e.GrantedAt, e.IsUndetailedReserve, observedAt = e.Account.Snapshot?.FetchedAt, e.Account.IsStale }),
                // Prefilled Google Calendar events, as « Ajouter une seule échéance » opens them.
                calendarEvents = AgentQueries.CalendarEntries(state, _preferences.Current, null, DateTimeOffset.UtcNow).Select(c => new { c.Title, startsAt = c.StartsAt, googleEventLink = CalendarExport.GoogleEventLink(c) }) };
            case "history": return new { revision, entries = _window.Reminders.History.OrderByDescending(h => h.UpdatedAt).Take(200).ToArray() };
            case "preferences": return new { revision, settings = General.From(_preferences.Current), phone = _preferences.Current.PhonePolicy,
                startWithWindows = !_demo && StartupSettings.IsEnabled,
                trayQuota = new { provider = _preferences.Current.TrayProvider.ToString(), window = _preferences.Current.TrayWindow.ToString(), perTool = _preferences.Current.TrayWindows },
                accountNames = _preferences.Current.Appearances.Where(a => a.Value.Name is not null).ToDictionary(a => a.Key, a => a.Value.Name),
                resetAlerts = _preferences.Current.ResetAlerts, pillHidden = _preferences.Current.PillHidden,
                avatars = _preferences.Current.Appearances.Where(a => a.Value.AvatarFile is not null).Select(a => a.Key),
                languagePendingRestart = Loc.Resolve(_preferences.Current.Language) != Loc.Current,
                sortMode = "reserved: no effect" };
            case "rules": return new { revision, rules = _preferences.Current.ReminderRules };
            case "channels":
                var s = _window.Reminders.Secrets.Read();
                return new { revision, twilio = new { s.Twilio.Enabled, hasCredentials = s.Twilio.Secret.Length > 0, s.Twilio.SmsFrom, s.Twilio.CallFrom, s.Twilio.To }, sendGrid = new { s.SendGrid.Enabled, hasCredentials = s.SendGrid.ApiKey.Length > 0, s.SendGrid.From, s.SendGrid.To },
                    // The topic is the subscription secret: write-only, like keys.
                    ntfy = new { s.Push.Enabled, s.Push.Server, hasTopic = s.Push.Topic.Length > 0, hasToken = s.Push.Token.Length > 0, mirrorWindows = _preferences.Current.PushMirror } };
            case "action": return Public(_actions.Find(Read<ActionQuery>(args).RequestId) ?? throw new ArgumentException("Action inconnue."));
            case "refresh":
                if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any()) throw new ArgumentException("Cette commande ne prend aucun paramètre.");
                var refreshed = await _window.TrackerService.RefreshWithResultAsync();
                return RefreshReport(_window.TrackerService, Revision, refreshed);
            case "show": _window.OpenPage(Read<PageQuery>(args).Page); return new { opened = true };
            case "usage_history":
                var history = Read<HistoryQuery>(args);
                if (history.Hours is not (24 or 168) || history.Window is not (ResetKind.Weekly or ResetKind.Short)) throw new ArgumentException("hours 24 ou 168, window Weekly ou Short.");
                if (!state.Accounts.Any(a => a.Profile.Id == history.AccountId)) throw new ArgumentException("Compte inconnu.");
                return AgentQueries.History(_window.TrackerService, history.AccountId, history.Hours, history.Window, DateTimeOffset.UtcNow);
            case "global_feed": return AgentQueries.GlobalFeed(state, DateTimeOffset.UtcNow);
            case "update_status": return AgentQueries.Updates(_updates);
            case "claude_setup": return AgentQueries.ClaudeCodeSetup(_preferences, _demo);
            case "whats_new":
                return new { revision, lastSeen = _preferences.Current.LastSeenVersion, current = _updates.CurrentVersion,
                    unseen = WhatsNew.Since(_preferences.Current.LastSeenVersion, _updates.CurrentVersion), all = WhatsNew.Items() };
            case "diagnostic":
                var report = _window.BuildDiagnostic();
                return new { revision, diagnostic = report, reportUrl = SupportDiagnostics.ReportUrl(report) };
            case "google":
                var calendar = _window.Calendar;
                return new
                {
                    revision, available = calendar.IsAvailable, connected = calendar.IsConnected, email = calendar.Email, canMail = calendar.CanMail,
                    connecting = calendar.IsConnecting, ownClient = calendar.HasOwnClient, bundledClient = GoogleCalendarSync.Bundled is not null,
                    problem = calendar.Problem?.ToString(), error = calendar.Error, projectId = calendar.ProjectId,
                    detectedClientFile = calendar.HasOwnClient || _demo ? null : calendar.FindClientFile()?.Path,
                    autoAdd = _preferences.Current.GoogleCalendarAutoAdd, mailAlerts = _preferences.Current.ReserveMailAlerts,
                    reserves = GoogleCalendarSync.HeldCredits(state, DateTimeOffset.UtcNow).Select(c => new
                    {
                        accountId = c.Value.AccountId, creditId = c.Value.CreditId,
                        expiresAt = state.Accounts.FirstOrDefault(a => a.Profile.Id == c.Value.AccountId)?.Snapshot?.ResetCredits?.FirstOrDefault(x => x.Id == c.Value.CreditId)?.ExpiresAt,
                        status = calendar.Link(c.Key)?.Status.ToString() ?? "Missing", link = calendar.Link(c.Key)?.HtmlLink
                    }).OrderBy(r => r.expiresAt)
                };
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
                if (p.Language is { } language && !Enum.IsDefined(language)) throw new ArgumentException("Langue inconnue.");
                var candidate = p.Apply(_preferences.Current);
                ApplicationCommands.ValidatePhone(candidate.PhonePolicy);
                run = () => { _commands.SavePreferences(p.Apply); return Task.FromResult("Réglages enregistrés."); };
                summary = Loc.T("Modifier les réglages généraux."); break;
            case "set_phone":
                var phone = Read<PhonePolicy>(request.Value); ApplicationCommands.ValidatePhone(phone);
                confirm = ApplicationCommands.ExpandsPhone(_preferences.Current.PhonePolicy, phone);
                summary = Loc.F("Limites : {0} SMS et {1} appels / jour.\nSilence : {2} ({3}).", phone.SmsPerDay, phone.CallsPerDay,
                    phone.QuietEnabled ? $"{phone.QuietStart}:00–{phone.QuietEnd}:00" : Loc.T("désactivé"), phone.TimeZoneId);
                run = () => { _commands.SavePreferences(p => p with { PhonePolicy = phone }); return Task.FromResult("Limites enregistrées."); }; break;
            case "set_rules":
                var rules = Read<ReminderRule[]>(request.Value); ApplicationCommands.ValidateRules(rules);
                if (rules.Any(r => r.AccountIds?.Any(id => !state.Accounts.Any(a => a.Profile.Id == id)) == true)) throw new ArgumentException("Compte inconnu.");
                confirm = ApplicationCommands.ExpandsExternalRules(_preferences.Current.ReminderRules!, rules);
                summary = Loc.T("Remplacer les règles de rappel :") + "\n" + string.Join("\n", rules.Where(r => r.Enabled && r.Channels.Length > 0).Select(r =>
                    Loc.F("{0} · {1} min avant · {2} · {3}", ReminderPlanner.Label(r.Kind), string.Join(", ", r.LeadMinutes), string.Join(", ", r.Channels),
                        r.AccountIds is null ? Loc.T("tous les comptes") : string.Join(", ", state.Accounts.Where(a => r.AccountIds.Contains(a.Profile.Id)).Select(a => a.Profile.Email))))) + Destinations();
                run = () => { _commands.SavePreferences(p => p with { ReminderRules = rules }); return Task.FromResult("Règles enregistrées."); }; break;
            case "set_channel":
                var change = Read<ChannelChange>(request.Value);
                var old = _window.Reminders.Secrets.Read(); var next = old;
                var given = new object?[] { change.Twilio, change.SendGrid, change.Ntfy }.Count(c => c is not null);
                if (given != 1) throw new ArgumentException("Fournir un seul connecteur : twilio, sendgrid ou ntfy.");
                if (change.Provider == "twilio" && change.Twilio is not null) next = old with { Twilio = change.Twilio };
                else if (change.Provider == "sendgrid" && change.SendGrid is not null) next = old with { SendGrid = change.SendGrid };
                else if (change.Provider == "ntfy" && change.Ntfy is not null) next = old with { Ntfy = change.Ntfy };
                else throw new ArgumentException("Fournir un seul connecteur : twilio, sendgrid ou ntfy.");
                // A complete replacement is explicit: omitted Enabled remains false, so saving keys never enables delivery.
                confirm = next.Twilio.Enabled && next.Twilio != old.Twilio || next.SendGrid.Enabled && next.SendGrid != old.SendGrid || next.Push.Enabled && next.Push != old.Push;
                summary = change.Provider switch
                {
                    "twilio" => Loc.F("Twilio · {0}\nSMS depuis {1}\nAppels depuis {2}\nDestinataire : {3}\nIdentifiants : masqués.", next.Twilio.Enabled ? Loc.T("activer / modifier") : Loc.T("désactiver"), next.Twilio.SmsFrom, next.Twilio.CallFrom, next.Twilio.To),
                    "sendgrid" => Loc.F("SendGrid · {0}\nDe : {1}\nVers : {2}\nClé : masquée.", next.SendGrid.Enabled ? Loc.T("activer / modifier") : Loc.T("désactiver"), next.SendGrid.From, next.SendGrid.To),
                    _ => Loc.F("Téléphone (ntfy) · {0}\nServeur : {1}\nSujet : {2}\nVos notifications seront publiées sur ce sujet.", next.Push.Enabled ? Loc.T("activer / modifier") : Loc.T("désactiver"), next.Push.Server, next.Push.Topic)
                };
                run = () => { _commands.SaveSecrets(next); return Task.FromResult("Connecteur enregistré ; aucun envoi effectué."); }; break;
            case "delete_credentials":
                var provider = Read<ProviderQuery>(request.Value).Provider;
                if (provider is not ("twilio" or "sendgrid" or "ntfy")) throw new ArgumentException("Connecteur inconnu.");
                run = () => { var secrets = _window.Reminders.Secrets.Read(); _commands.SaveSecrets(provider switch { "twilio" => secrets with { Twilio = new() }, "sendgrid" => secrets with { SendGrid = new() }, _ => secrets with { Ntfy = new() } }); return Task.FromResult("Identifiants supprimés ; canal désactivé."); };
                summary = Loc.F("Supprimer les identifiants {0}", provider); break;
            case "google_connect":
                var mail = Read<GoogleConnectQuery>(request.Value).Mail;
                if (!_window.Calendar.IsAvailable) throw new InvalidOperationException("Google Agenda indisponible dans cette instance.");
                // The Google consent page is the confirmation: nothing is granted until the user accepts it there.
                run = () => { _window.StartGoogleConnection(mail); return Task.FromResult("Autorisation ouverte dans le navigateur de l’utilisateur ; suivre avec get_google_calendar."); };
                summary = Loc.T("Connecter Google Agenda"); break;
            case "google_disconnect":
                run = async () => { await _window.Calendar.DisconnectAsync(); return "Google Agenda déconnecté ; les événements créés restent dans Google."; };
                summary = Loc.T("Déconnecter Google Agenda"); break;
            case "google_import":
                var file = Read<PathQuery>(request.Value).Path;
                if (!File.Exists(file) || new FileInfo(file).Length > 65_536) throw new ArgumentException("Fichier client introuvable ou trop grand.");
                run = () => { _window.Calendar.ImportClient(File.ReadAllText(file)); return Task.FromResult("Client Google importé ; appeler connect_google_calendar."); };
                summary = Loc.T("Importer un client Google"); break;
            case "google_sync":
                run = async () => { await _window.SyncCalendarAsync(); return _window.Calendar.Error ?? "Synchronisation terminée."; };
                summary = Loc.T("Synchroniser Google Agenda"); break;
            case "google_add":
                var credit = Read<CreditQuery>(request.Value);
                var account = state.Accounts.FirstOrDefault(a => a.Profile.Id == credit.AccountId) ?? throw new ArgumentException("Compte inconnu.");
                var expires = account.Snapshot?.ResetCredits?.FirstOrDefault(c => c.Id == credit.CreditId)?.ExpiresAt ?? throw new ArgumentException("Reset en réserve inconnu ou sans date.");
                run = async () => { await _window.AddCreditToCalendarAsync(GoogleCalendarSync.Key(credit.AccountId, credit.CreditId, expires), throwErrors: true); return "Événement présent dans Google Agenda."; };
                summary = Loc.T("Ajouter un reset en réserve à Google Agenda"); break;
            case "set_tray_quota":
                var tray = Read<TrayQuotaQuery>(request.Value);
                if (!Enum.IsDefined(tray.Provider) || tray.Window is not (ResetKind.Weekly or ResetKind.Short)) throw new ArgumentException("Outil ou fenêtre inconnus.");
                run = () => { _commands.SetTrayQuota(tray.Provider, tray.Window); return Task.FromResult("Quota de l’icône enregistré."); };
                summary = Loc.T("Choisir le quota affiché dans l’icône"); break;
            case "set_startup":
                var startup = Read<EnabledQuery>(request.Value).Enabled;
                run = () => { if (!_demo) StartupSettings.SetEnabled(startup); return Task.FromResult(startup ? "Démarrage avec Windows activé." : "Démarrage avec Windows désactivé."); };
                summary = Loc.T("Démarrer avec Windows"); break;
            case "declare_reset":
                var declared = Read<DeclareQuery>(request.Value).At;
                run = () => { _commands.DeclareCodexReset(declared, DateTimeOffset.UtcNow, revision); return Task.FromResult(declared is null ? "Reset déclaré annulé." : "Reset général Codex déclaré ; un nouveau relevé reste prioritaire."); };
                summary = Loc.T("Déclarer un reset général Codex"); break;
            case "set_reset_alert":
                var bell = Read<ResetAlertQuery>(request.Value);
                run = () => { _commands.SetResetAlert(state, bell.AccountId, bell.Kind, bell.At, bell.Enabled, DateTimeOffset.UtcNow, revision); return Task.FromResult(bell.Enabled ? "Notification de reset activée." : "Notification de reset retirée."); };
                summary = Loc.T("Notification d’un reset"); break;
            case "rename_account":
                var naming = Read<AccountNameQuery>(request.Value);
                if (!state.Accounts.Any(a => a.Profile.Id == naming.AccountId)) throw new ArgumentException("Compte inconnu.");
                var name = string.IsNullOrWhiteSpace(naming.Name) ? null : naming.Name.Trim();
                if (name is { Length: > 60 }) throw new ArgumentException("Nom trop long (60 caractères au plus).");
                run = () =>
                {
                    _commands.SavePreferences(pref =>
                    {
                        var appearances = new Dictionary<Guid, AccountAppearance>(pref.Appearances);
                        var current = appearances.GetValueOrDefault(naming.AccountId) ?? new();
                        appearances[naming.AccountId] = current with { Name = name };
                        return pref with { Appearances = appearances };
                    });
                    return Task.FromResult(name is null ? "Nom d’affichage retiré." : "Nom d’affichage enregistré.");
                };
                summary = Loc.T("Renommer un compte"); break;
            case "remove_account":
                var removed = Read<AccountQuery>(request.Value).AccountId;
                var target = state.Accounts.FirstOrDefault(a => a.Profile.Id == removed) ?? throw new ArgumentException("Compte inconnu.");
                if (target.IsActive) throw new ArgumentException("Le compte actif ne peut pas être retiré.");
                // Removing an account deletes its local history: always confirmed in the tracker.
                confirm = true;
                summary = Loc.F("Retirer le compte {0} ({1}) et son historique local du tracker ?", target.Profile.Email, target.Profile.ProviderName);
                run = async () => { await AccountRemoval.RemoveAsync(_window.TrackerService, _preferences, removed); return "Compte retiré du tracker, avec son nom et sa photo."; }; break;
            case "detect_account":
                run = async () => { await _window.TrackerService.ImportCurrentAccountAsync(); return "Détection du compte actif terminée."; };
                summary = Loc.T("Détecter le compte actif"); break;
            case "complete_onboarding":
                run = async () => { await _window.TrackerService.CompleteOnboardingAsync(); return "Accueil terminé."; };
                summary = Loc.T("Terminer l’accueil"); break;
            case "dismiss_notice":
                var noticeKind = Read<NoticeQuery>(request.Value).Kind;
                var notices = AgentQueries.Notices(state, _preferences.Current, DateTimeOffset.UtcNow);
                var notice = notices.FirstOrDefault(n => n.Kind == noticeKind) ?? throw new ArgumentException("Aucun encart de ce type n’est affiché.");
                run = () =>
                {
                    if (notice.Kind == "manualReset") _commands.SavePreferences(p => p with { DismissedManualCodexReset = p.ManualCodexReset?.DeclaredAt });
                    else _commands.SavePreferences(p => p with { DismissedNotices = AgentQueries.Dismissing(p, notices, notice.Keys) });
                    return Task.FromResult("Encart masqué jusqu’au prochain événement.");
                };
                summary = Loc.T("Masquer un encart"); break;
            case "hide_pill":
                var pillKey = Read<KeyQuery>(request.Value).Key;
                if (string.IsNullOrWhiteSpace(pillKey) || pillKey.Length > 200) throw new ArgumentException("Clé de pastille invalide (lue dans get_status.pill).");
                run = () => { _commands.SavePreferences(p => p with { PillHidden = [.. p.PillHidden.Where(k => k != pillKey).TakeLast(19), pillKey] }); return Task.FromResult("Pastille masquée jusqu’à la prochaine alerte."); };
                summary = Loc.T("Masquer la pastille"); break;
            case "set_avatar":
                var avatar = Read<AvatarQuery>(request.Value);
                if (!state.Accounts.Any(a => a.Profile.Id == avatar.AccountId)) throw new ArgumentException("Compte inconnu.");
                // Read and checked now (PNG or JPEG, 8 MB, 40 megapixels), like the window's file picker.
                var image = string.IsNullOrWhiteSpace(avatar.Path) ? null : AvatarStore.ReadImage(avatar.Path);
                run = () =>
                {
                    var file = image is null ? null : AvatarStore.Save(_preferences.DataDirectory, image);
                    var previous = _preferences.Current.Appearances.GetValueOrDefault(avatar.AccountId)?.AvatarFile;
                    _commands.SavePreferences(p =>
                    {
                        var appearances = new Dictionary<Guid, AccountAppearance>(p.Appearances);
                        appearances[avatar.AccountId] = (appearances.GetValueOrDefault(avatar.AccountId) ?? new()) with { AvatarFile = file };
                        return p with { Appearances = appearances };
                    });
                    AvatarStore.Remove(_preferences.DataDirectory, previous);
                    return Task.FromResult(file is null ? "Photo retirée : initiales affichées." : "Photo du compte enregistrée.");
                };
                summary = Loc.T("Changer la photo d’un compte"); break;
            case "export_ics":
                var ics = Read<IcsQuery>(request.Value);
                var entries = AgentQueries.CalendarEntries(state, _preferences.Current, ics.AccountId, DateTimeOffset.UtcNow);
                if (entries.Count == 0) throw new ArgumentException("Aucune échéance à exporter.");
                var text = CalendarExport.Serialize(entries, DateTimeOffset.UtcNow);
                if (ics.Path is { } icsPath && (!Path.IsPathFullyQualified(icsPath) || File.Exists(icsPath) || !icsPath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("Chemin .ics absolu et nouveau attendu : un fichier existant n’est jamais remplacé.");
                run = () =>
                {
                    if (ics.Path is null) return Task.FromResult(text);
                    if (_preferences.IsPersistent) try { AccountLink.EnsureRegistered(AppPackage.LaunchPath); } catch (Exception) { /* The file stays usable without the link. */ }
                    File.WriteAllText(ics.Path, text, new System.Text.UTF8Encoding(false));
                    return Task.FromResult($"{entries.Count} échéances exportées dans {ics.Path}.");
                };
                summary = Loc.T("Exporter les échéances"); break;
            case "prepare_google_import":
                var import = Read<AccountFilterQuery>(request.Value);
                var importEntries = AgentQueries.CalendarEntries(state, _preferences.Current, import.AccountId, DateTimeOffset.UtcNow);
                if (importEntries.Count == 0) throw new ArgumentException("Aucune échéance à exporter.");
                run = () =>
                {
                    var directory = Path.Combine(_preferences.DataDirectory, "calendar-exports"); Directory.CreateDirectory(directory);
                    var file = Path.Combine(directory, $"CodexTracker-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.ics");
                    File.WriteAllText(file, CalendarExport.Serialize(importEntries, DateTimeOffset.UtcNow), new System.Text.UTF8Encoding(false));
                    return Task.FromResult(JsonSerializer.Serialize(new { path = file, importPage = "https://calendar.google.com/calendar/r/settings/export" }));
                };
                summary = Loc.T("Préparer l’import dans Google Agenda"); break;
            case "check_announcements":
                if (_demo || !_preferences.Current.MonitorGlobalResets) throw new InvalidOperationException("Surveillance des annonces désactivée (ou démonstration).");
                run = async () => { await _window.TrackerService.CheckGlobalResetAnnouncementsAsync(); return JsonSerializer.Serialize(AgentQueries.GlobalFeed(_window.TrackerService.State, DateTimeOffset.UtcNow), Json); };
                summary = Loc.T("Vérifier les annonces"); break;
            case "forget_google_client":
                if (!_window.Calendar.HasOwnClient) throw new ArgumentException("Aucun client Google importé.");
                confirm = true;
                run = () => { _window.Calendar.ForgetClient(); return Task.FromResult("Client Google importé supprimé ; le client intégré reprend la main."); };
                summary = Loc.T("Supprimer le client Google importé ? La connexion qui l’utilise est coupée."); break;
            case "check_update":
                if (_demo) throw new InvalidOperationException("Mises à jour désactivées dans la démonstration.");
                if (AppPackage.IsPackaged) throw new InvalidOperationException("Édition Microsoft Store : les mises à jour arrivent par le Store.");
                run = async () =>
                {
                    var result = await _updates.CheckDetailedAsync();
                    if (result.Release is { } release && UpdateService.CanSelfUpdate && _preferences.Current.DownloadUpdatesAutomatically) await _updates.PrepareAsync(release);
                    return JsonSerializer.Serialize(AgentQueries.Updates(_updates), Json);
                };
                summary = Loc.T("Rechercher une mise à jour"); break;
            case "install_update":
                if (_demo || !UpdateService.CanSelfUpdate) throw new InvalidOperationException("Installation indisponible ici (démonstration, développement ou édition Store).");
                if (_updates.Prepared is null) throw new ArgumentException("Aucune mise à jour prête : appeler check_for_update avec le téléchargement automatique.");
                confirm = true;
                run = () => { Later(() => _window.InstallReadyUpdateAsync()); return Task.FromResult("Installation de la mise à jour lancée ; le tracker redémarre."); };
                summary = Loc.F("Installer la version {0} et redémarrer le tracker ?", _updates.Prepared.Release.Version); break;
            case "quit":
                confirm = true;
                run = () => { Later(() => ((App)System.Windows.Application.Current).ExitAsync()); return Task.FromResult("Fermeture du tracker demandée."); };
                summary = Loc.T("Quitter Codex Tracker ? Le suivi et les rappels s’arrêtent jusqu’au prochain lancement."); break;
            case "restart":
                confirm = true;
                run = () =>
                {
                    Process.Start(new ProcessStartInfo(AppPackage.LaunchPath, $"--relaunch-after {Environment.ProcessId}") { UseShellExecute = false });
                    Later(() => ((App)System.Windows.Application.Current).ExitAsync());
                    return Task.FromResult("Redémarrage du tracker demandé.");
                };
                summary = Loc.T("Redémarrer Codex Tracker ?"); break;
            case "acknowledge_recovery":
                if (!_preferences.RecoveryRequired) throw new ArgumentException("Aucun réglage récupéré à valider.");
                confirm = true;
                run = () => { _preferences.AcknowledgeRecovery(); return Task.FromResult("Réglages récupérés validés ; les rappels coupés restent à réactiver."); };
                summary = Loc.T("Valider les réglages récupérés ?"); break;
            case "dismiss_whats_new":
                run = () => { _commands.SavePreferences(p => p with { LastSeenVersion = _updates.CurrentVersion }); return Task.FromResult("Nouveautés vues."); };
                summary = Loc.T("Masquer les nouveautés"); break;
            case "export_profile":
                var exportQuery = Read<ProfileQuery>(request.Value);
                if (_demo) throw new InvalidOperationException("Export de profil indisponible dans la démonstration.");
                if (!Path.IsPathFullyQualified(exportQuery.Path) || File.Exists(exportQuery.Path)) throw new ArgumentException("Chemin absolu et nouveau attendu : un fichier existant n’est jamais remplacé.");
                if (exportQuery.Password is not { Length: >= 8 }) throw new ArgumentException("Mot de passe d’au moins 8 caractères.");
                confirm = true;
                run = async () =>
                {
                    var archive = await Task.Run(() => ProfileTransfer.Export(_window.TrackerService, _preferences, exportQuery.Password, DateTimeOffset.UtcNow));
                    await File.WriteAllBytesAsync(exportQuery.Path, archive);
                    return $"Profil chiffré exporté ({_window.TrackerService.State.Accounts.Count} comptes).";
                };
                summary = Loc.F("Exporter le profil chiffré (comptes, historique, réglages) vers {0} ?", exportQuery.Path); break;
            case "import_profile":
                var importQuery = Read<ProfileQuery>(request.Value);
                if (_demo) throw new InvalidOperationException("Import de profil indisponible dans la démonstration.");
                if (!File.Exists(importQuery.Path) || new FileInfo(importQuery.Path).Length > ProfileArchive.MaximumBytes) throw new ArgumentException("Fichier de profil introuvable ou trop volumineux.");
                // Decrypted now: a wrong password fails before the local confirmation.
                var bundle = await Task.Run(async () => ProfileTransfer.Read(await File.ReadAllBytesAsync(importQuery.Path), importQuery.Password));
                confirm = true;
                run = async () => { var result = await ProfileTransfer.ImportAsync(bundle, _window.TrackerService, _commands, _preferences); return $"{result.Added} comptes ajoutés, {result.Updated} mis à jour."; };
                summary = Loc.F("Importer ce profil ? {0} comptes, exportés le {1:dd/MM/yyyy à HH:mm}. Ses réglages, rappels, noms et photos remplacent ceux de ce PC.", bundle.Profiles.Accounts.Count, bundle.ExportedAt.ToLocalTime()); break;
            case "test":
                var channel = Read<TestQuery>(request.Value).Channel;
                if (!Enum.IsDefined(channel)) throw new ArgumentException("Canal inconnu.");
                // An email from the user's Gmail to the same address costs nothing and reaches nobody else: no local confirmation.
                var gmailToSelf = channel == ReminderChannel.Email && _window.Calendar.CanMail &&
                    !NotificationProviders.Configured(ReminderChannel.Email, _window.Reminders.Secrets.Read());
                // A test on the user's own ntfy topic, already confirmed when it was set up, costs nothing.
                confirm = channel is not (ReminderChannel.Windows or ReminderChannel.Push) && !gmailToSelf;
                summary = Loc.F("Envoyer un test {0}. Les frais éventuels sont facturés par votre prestataire.", channel) + Destinations() + "\n" + Loc.T("Message : test de rappel Codex Tracker. Les limites et heures silencieuses restent appliquées.");
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
        return "\n" + Loc.F("Destinataire SMS/appels : {0}\nDestinataire email : {1}", s.Twilio.To, s.SendGrid.To);
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
        var dialog = new TrackerDialog(_window, Loc.T("Demande de l’assistant"), summary + "\n\n" + Loc.T("Expire après cinq minutes. Aucun envoi avant votre confirmation."), Loc.T("Confirmer"), Loc.T("Annuler"));
        _dialogs[id] = dialog;
        var accepted = dialog.ShowDialog() == true; _dialogs.Remove(id);
        if (!_pending.ContainsKey(id)) return;
        if (!accepted) { Cancel(id, "Refusé ou fenêtre fermée."); return; }
        if (_actions.Find(id)?.Status != "pending" || pending.Revision != Revision || !_preferences.Current.McpEnabled) { Cancel(id, "Confirmation expirée ou réglages modifiés."); return; }
        try { await Execute(id, pending.Run); }
        catch { _window.AssistantError = Loc.T("MCP suspendu : impossible de persister le résultat. Aucun nouvel envoi autorisé."); }
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
        catch { _window.AssistantError = Loc.T("MCP suspendu : journal local indisponible."); _timer.Stop(); _pending.Clear(); foreach (var dialog in _dialogs.Values.ToArray()) dialog.Close(); }
    }
    internal static RefreshReply RefreshReport(ITrackerService service, string revision, IReadOnlyList<ProviderRefreshStatus>? completed = null)
    {
        var state = service.State;
        var active = state.ActiveAccounts.Select(a => a.Profile.Id).ToHashSet();
        var providers = (completed ?? service.GetRefreshStatus()).Select(status =>
        {
            // Demo/default implementations expose historical snapshots without performing a
            // collection. Neither those snapshots nor an unfinished attempt prove success.
            var outcome = (status.Outcome is RefreshOutcome.Updated or RefreshOutcome.Unchanged) &&
                (status.AttemptedAt is null || status.CompletedAt is null) ? RefreshOutcome.Unavailable : status.Outcome;
            var error = status.ErrorCode is null ? outcome == RefreshOutcome.Failed ? "collection_failed" : null :
                status.ErrorCode == "account_changed" ? "accountChanged" :
                Enum.TryParse<CodexFailureCode>(status.ErrorCode, out var code) && Enum.IsDefined(code) && code != CodexFailureCode.None
                    ? Name(code) : "collection_failed";
            return new ProviderRefreshReply(status.Provider.ToString(), status.AccountId, Name(outcome), Name(status.Reason),
                status.AttemptedAt, status.CompletedAt, status.ObservedAt, status.DurationMilliseconds,
                status.NextAttemptAt, status.ConsecutiveFailures, error);
        }).ToArray();
        var current = providers.Where(p => p.AccountId is { } id && active.Contains(id)).ToArray();
        var succeeded = current.Count(p => p.Outcome is "updated" or "unchanged");
        var suspended = providers.Any(p => p.Outcome == "suspended");
        var failed = current.Any(p => p.Outcome == "failed");
        var status = suspended ? "suspended" : succeeded > 0
            ? succeeded == active.Count && !failed ? "success" : "partial"
            : failed ? "failed" : "unavailable";
        return new(status == "success", status, revision, state.ActiveAccount?.Snapshot?.FetchedAt, providers);
        static string Name<T>(T value) where T : Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    }
    /// <summary>Runs after the reply is on its way: quitting, restarting or installing ends this process.</summary>
    private void Later(Func<Task> action) => _ = _window.Dispatcher.InvokeAsync(async () => { await Task.Delay(800); await action(); });
    private static object Public(AssistantAction a) => new { requestId = a.Id, a.Status, a.ExpiresAt, a.Detail };
    private static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw new ArgumentException("Paramètres manquants.");
    public void Dispose()
    {
        _disposed = true; _timer.Stop(); _preferences.Changed -= Changed;
        foreach (var id in _pending.Keys.ToArray()) Cancel(id, "Application fermée.");
    }
}

internal sealed record ProviderRefreshReply(string Provider, Guid? AccountId, string Outcome, string Reason,
    DateTimeOffset? AttemptedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ObservedAt,
    double? DurationMilliseconds, DateTimeOffset? NextAttemptAt, int ConsecutiveFailures, string? ErrorCode);
internal sealed record RefreshReply(bool Refreshed, string Status, string Revision, DateTimeOffset? ObservedAt,
    IReadOnlyList<ProviderRefreshReply> Providers);
internal sealed record ChangeRequest(string RequestId, string ExpectedRevision, JsonElement Value);
internal sealed record ActionQuery(string RequestId);
internal sealed record PageQuery(string Page);
internal sealed record ProviderQuery(string Provider);
internal sealed record TestQuery(ReminderChannel Channel);
internal sealed record GoogleConnectQuery(bool Mail);
internal sealed record PathQuery(string Path);
internal sealed record CreditQuery(Guid AccountId, string CreditId);
internal sealed record TrayQuotaQuery(AccountProvider Provider, ResetKind Window);
internal sealed record EnabledQuery(bool Enabled);
internal sealed record DeclareQuery(DateTimeOffset? At);
internal sealed record ResetAlertQuery(Guid AccountId, ResetKind Kind, DateTimeOffset At, bool Enabled);
internal sealed record AccountNameQuery(Guid AccountId, string? Name);
internal sealed record AccountQuery(Guid AccountId);
internal sealed record HistoryQuery(Guid AccountId, int Hours, ResetKind Window);
internal sealed record NoticeQuery(string Kind);
internal sealed record KeyQuery(string Key);
internal sealed record AvatarQuery(Guid AccountId, string? Path);
internal sealed record IcsQuery(Guid? AccountId, string? Path);
internal sealed record AccountFilterQuery(Guid? AccountId);
internal sealed record ProfileQuery(string Path, string Password);
internal sealed record ChannelChange(string Provider, TwilioSettings? Twilio = null, SendGridSettings? SendGrid = null, NtfySettings? Ntfy = null);
/// <summary>General settings. The calendar, email and credit notice switches are optional: omitted, they keep their value.</summary>
internal sealed record General(ThemeMode ThemeMode, SortMode SortMode, bool Alert20, bool Alert10, bool Alert5, bool ResetNotifications, bool HoverPreview, int RefreshMinutes, bool AdaptiveRefresh,
    bool? NewCreditNotifications = null, bool? ReserveAdviceNotifications = null, bool? ReserveMailAlerts = null, bool? GoogleCalendarAutoAdd = null, bool? CalendarSuggestionDismissed = null, bool? ForecastNotifications = null,
    bool? PushMirror = null, bool? ScreenPill = null, AppLanguage? Language = null, bool? TrayIconPerTool = null, bool? MonitorGlobalResets = null,
    bool? DownloadUpdatesAutomatically = null, bool? InstallUpdatesAtStartup = null, bool? IncludePrereleaseUpdates = null, bool? SmartReminders = null)
{
    internal static General From(TrackerPreferences p) => new(p.ThemeMode, p.SortMode, p.Alert20, p.Alert10, p.Alert5, p.ResetNotifications, p.HoverPreview, p.RefreshMinutes, p.AdaptiveRefresh,
        p.NewCreditNotifications, p.ReserveAdviceNotifications, p.ReserveMailAlerts, p.GoogleCalendarAutoAdd, p.CalendarSuggestionDismissed, p.ForecastNotifications, p.PushMirror, p.ScreenPill,
        p.Language, p.TrayIconPerTool, p.MonitorGlobalResets, p.DownloadUpdatesAutomatically, p.InstallUpdatesAtStartup, p.IncludePrereleaseUpdates, p.SmartReminders);
    internal TrackerPreferences Apply(TrackerPreferences p) => p with
    {
        ThemeMode = ThemeMode, SortMode = SortMode, Alert20 = Alert20, Alert10 = Alert10, Alert5 = Alert5, ResetNotifications = ResetNotifications, HoverPreview = HoverPreview, RefreshMinutes = RefreshMinutes, AdaptiveRefresh = AdaptiveRefresh,
        NewCreditNotifications = NewCreditNotifications ?? p.NewCreditNotifications, ReserveAdviceNotifications = ReserveAdviceNotifications ?? p.ReserveAdviceNotifications,
        ReserveMailAlerts = ReserveMailAlerts ?? p.ReserveMailAlerts, GoogleCalendarAutoAdd = GoogleCalendarAutoAdd ?? p.GoogleCalendarAutoAdd,
        CalendarSuggestionDismissed = CalendarSuggestionDismissed ?? p.CalendarSuggestionDismissed, ForecastNotifications = ForecastNotifications ?? p.ForecastNotifications,
        PushMirror = PushMirror ?? p.PushMirror, ScreenPill = ScreenPill ?? p.ScreenPill,
        Language = Language ?? p.Language, TrayIconPerTool = TrayIconPerTool ?? p.TrayIconPerTool, MonitorGlobalResets = MonitorGlobalResets ?? p.MonitorGlobalResets,
        DownloadUpdatesAutomatically = DownloadUpdatesAutomatically ?? p.DownloadUpdatesAutomatically, InstallUpdatesAtStartup = InstallUpdatesAtStartup ?? p.InstallUpdatesAtStartup,
        IncludePrereleaseUpdates = IncludePrereleaseUpdates ?? p.IncludePrereleaseUpdates, SmartReminders = SmartReminders ?? p.SmartReminders
    };
}
