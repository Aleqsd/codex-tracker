using System.Diagnostics;
using CodexTracker.Codex;

namespace CodexTracker.App;

internal static class ReminderSettingsView
{
    private static string ChannelName(ReminderChannel channel) => channel switch { ReminderChannel.Windows => "Windows", ReminderChannel.Sms => "SMS", ReminderChannel.Call => "Appel", _ => "Email" };
    private static string LeadName(int minutes) => minutes < 60 ? $"{minutes} min" : minutes <= 1440 ? $"{minutes / 60} h" : $"{minutes / 1440} j";
    private static TextBlock Hint(string text) { var hint = Ui.Text(text, 11, "MutedBrush"); hint.Margin = new Thickness(0, 7, 0, 12); return hint; }
    private static StackPanel Panel() => new() { Margin = new Thickness(0, 8, 0, 18) };
    private static Button Button(string text) => new() { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 8, 0) };
    private static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 12, 6) };

    internal static FrameworkElement Rules(PreferencesStore preferences, ITrackerService service, ApplicationCommands commands)
    {
        var root = Panel(); root.Children.Add(Hint("Le tracker doit rester ouvert et le PC éveillé. Chaque canal est indépendant ; un rappel passé ne confirme pas un reset dans Codex."));
        foreach (var kind in Enum.GetValues<ResetKind>())
        {
            var baseline = preferences.Current.ReminderRules!.Where(r => r.Kind == kind).ToArray();
            var existing = preferences.Current.ReminderRules!.Where(r => r.Kind == kind).ToArray();
            var summary = Hint("");
            void Summary()
            {
                var active = preferences.Current.ReminderRules!.Where(r => r.Kind == kind && r.Enabled && r.Channels.Length > 0).ToArray();
                summary.Text = active.Length == 0 ? "Désactivé" : string.Join(" · ", active.SelectMany(r => r.LeadMinutes).Distinct().OrderDescending().Select(LeadName)) + "  /  " + string.Join(", ", active.SelectMany(r => r.Channels).Distinct().Select(ChannelName));
            }
            Summary();
            var content = Panel();
            var enabled = Check("Activer ces rappels", existing.Any(r => r.Enabled)); content.Children.Add(enabled);
            var accounts = Panel(); var all = Check("Tous les comptes, actuels et futurs", existing.FirstOrDefault()?.AccountIds is null); accounts.Children.Add(all);
            var accountChecks = service.State.Accounts.Select(a => (a.Profile.Id, Box: Check(a.Profile.Email, existing.FirstOrDefault()?.AccountIds?.Contains(a.Profile.Id) == true))).ToArray();
            foreach (var (_, box) in accountChecks) { box.IsEnabled = all.IsChecked != true; accounts.Children.Add(box); }
            content.Children.Add(new Expander { Header = "Comptes concernés", Content = accounts, Margin = new Thickness(0, 4, 0, 10) });
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            foreach (var _ in Enum.GetValues<ReminderChannel>()) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var channels = Enum.GetValues<ReminderChannel>();
            for (int c = 0; c < channels.Length; c++) { var label = Ui.Text(ChannelName(channels[c]), 11, "MutedBrush"); Grid.SetColumn(label, c + 1); grid.Children.Add(label); }
            var rows = new List<(int Minutes, CheckBox[] Boxes)>();
            foreach (var lead in ReminderPlanner.AllowedMinutes.Where(m => kind != ResetKind.Short || m < 300))
            {
                int row = grid.RowDefinitions.Count; grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = Ui.Text(LeadName(lead), 12); label.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(label, row); grid.Children.Add(label);
                var boxes = channels.Select(channel => Check("", existing.Any(r => r.LeadMinutes.Contains(lead) && r.Channels.Contains(channel)))).ToArray();
                for (int c = 0; c < boxes.Length; c++)
                {
                    Grid.SetColumn(boxes[c], c + 1); Grid.SetRow(boxes[c], row); grid.Children.Add(boxes[c]);
                    System.Windows.Automation.AutomationProperties.SetName(boxes[c], $"{ReminderPlanner.Label(kind)}, {LeadName(lead)}, {ChannelName(channels[c])}");
                }
                rows.Add((lead, boxes));
            }
            content.Children.Add(grid); content.Children.Add(Hint("SMS, appels et emails nécessitent un connecteur activé dans Canaux."));
            var status = Hint("");
            var save = Button("Enregistrer ces rappels");
            save.Click += (_, _) =>
            {
                try
                {
                    var ids = all.IsChecked == true ? null : accountChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Id).ToArray();
                    var updated = rows.Select(r => new ReminderRule(kind, enabled.IsChecked == true, [r.Minutes], channels.Where((_, i) => r.Boxes[i].IsChecked == true).ToArray(), ids));
                    if (System.Text.Json.JsonSerializer.Serialize(preferences.Current.ReminderRules!.Where(r => r.Kind == kind)) != System.Text.Json.JsonSerializer.Serialize(baseline)) { status.Text = "Règles modifiées ailleurs. Fermez puis rouvrez les réglages."; return; }
                    commands.SavePreferences(p => p with { ReminderRules = p.ReminderRules!.Where(r => r.Kind != kind).Concat(updated).ToArray() });
                    baseline = preferences.Current.ReminderRules!.Where(r => r.Kind == kind).ToArray(); status.Text = "Rappels enregistrés."; Summary();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "Impossible d’enregistrer les rappels."; }
            };
            all.Click += (_, _) => { foreach (var (_, box) in accountChecks) box.IsEnabled = all.IsChecked != true; };
            content.Children.Add(save); content.Children.Add(status);
            root.Children.Add(new Expander { Header = ReminderPlanner.Label(kind), Content = content, Margin = new Thickness(0, 8, 0, 0) });
            summary.Margin = new Thickness(18, 6, 0, 14); root.Children.Add(summary);
        }
        return root;
    }

    internal static FrameworkElement Channels(PreferencesStore preferences, ReminderRuntime runtime, bool demo, ApplicationCommands commands)
    {
        var root = Panel(); root.Children.Add(Hint("Facultatif : utilisez vos propres comptes. Les envois partent directement de ce PC ; les frais sont facturés par votre prestataire."));
        root.Children.Add(TestButton("Tester une notification Windows", ReminderChannel.Windows, runtime, demo, root));
        NotificationSecrets secrets;
        try { secrets = runtime.Secrets.Read(); }
        catch { root.Children.Add(Hint("Identifiants locaux illisibles. Aucun écrasement effectué.")); return root; }
        var twilio = Panel(); var enabled = Check("Activer Twilio", secrets.Twilio.Enabled); twilio.Children.Add(enabled);
        var sid = Field(twilio, "Account SID", secrets.Twilio.AccountSid);
        var key = Field(twilio, "API Key SID", secrets.Twilio.KeySid);
        var secret = Password(twilio, "API Key Secret", secrets.Twilio.Secret);
        var sms = Field(twilio, "Expéditeur SMS (numéro ou identifiant autorisé)", secrets.Twilio.SmsFrom);
        var call = Field(twilio, "Numéro Twilio autorisé pour les appels", secrets.Twilio.CallFrom);
        var to = Field(twilio, "Votre numéro destinataire (+33…)", secrets.Twilio.To);
        twilio.Children.Add(Hint("Vous pouvez configurer seulement les SMS ou seulement les appels. Les expéditeurs doivent être autorisés par Twilio pour votre destination."));
        var twilioStatus = Hint(secrets.Twilio.Enabled ? "Configuration enregistrée ; livraison non vérifiée." : "Désactivé");
        var save = Button("Enregistrer Twilio"); save.IsEnabled = !demo;
        save.Click += (_, _) =>
        {
            try
            {
                var config = new TwilioSettings(enabled.IsChecked == true, sid.Text.Trim(), key.Text.Trim(), secret.Password.Trim(), sms.Text.Trim(), call.Text.Trim(), to.Text.Trim());
                var current = runtime.Secrets.Read() with { Twilio = config };
                if (config.Enabled && !NotificationProviders.Configured(ReminderChannel.Sms, current) && !NotificationProviders.Configured(ReminderChannel.Call, current)) { twilioStatus.Text = "Vérifiez les identifiants, le destinataire et au moins un expéditeur."; return; }
                if (runtime.Secrets.Read().Twilio != secrets.Twilio) { twilioStatus.Text = "Configuration modifiée ailleurs. Rouvrez les réglages."; return; }
                commands.SaveSecrets(current); secrets = secrets with { Twilio = current.Twilio }; twilioStatus.Text = "Enregistré localement et chiffré. Aucun envoi effectué.";
            }
            catch { twilioStatus.Text = "Impossible d’enregistrer les identifiants."; }
        };
        twilio.Children.Add(save); twilio.Children.Add(twilioStatus);
        twilio.Children.Add(TestButton("Envoyer un SMS de test · payant", ReminderChannel.Sms, runtime, demo, twilio));
        twilio.Children.Add(TestButton("Recevoir un appel de test · payant", ReminderChannel.Call, runtime, demo, twilio));
        var remove = Button("Supprimer les identifiants Twilio"); remove.IsEnabled = !demo;
        remove.Click += (_, _) => { try { commands.SaveSecrets(runtime.Secrets.Read() with { Twilio = new() }); secrets = secrets with { Twilio = new() }; enabled.IsChecked = false; sid.Clear(); key.Clear(); secret.Clear(); sms.Clear(); call.Clear(); to.Clear(); twilioStatus.Text = "Identifiants supprimés ; canal désactivé."; } catch { twilioStatus.Text = "Suppression impossible."; } };
        twilio.Children.Add(remove); twilio.Children.Add(Link("Configurer Twilio ↗", "https://www.twilio.com/docs/usage/requests-to-twilio"));
        root.Children.Add(new Expander { Header = "Twilio · SMS et appels", Content = twilio, Margin = new Thickness(0, 16, 0, 10) });

        var email = Panel(); var emailEnabled = Check("Activer SendGrid", secrets.SendGrid.Enabled); email.Children.Add(emailEnabled);
        var emailKey = Password(email, "Clé API SendGrid · permission Mail Send", secrets.SendGrid.ApiKey);
        var from = Field(email, "Adresse d’expéditeur vérifiée", secrets.SendGrid.From);
        var recipient = Field(email, "Adresse destinataire", secrets.SendGrid.To);
        email.Children.Add(Hint("SendGrid utilise une clé et une facturation distinctes de Twilio. Vérifiez votre expéditeur et les tarifs avant activation."));
        var emailStatus = Hint(secrets.SendGrid.Enabled ? "Configuration enregistrée ; réception non vérifiée." : "Désactivé");
        var saveEmail = Button("Enregistrer SendGrid"); saveEmail.IsEnabled = !demo;
        saveEmail.Click += (_, _) =>
        {
            try
            {
                var config = new SendGridSettings(emailEnabled.IsChecked == true, emailKey.Password.Trim(), from.Text.Trim(), recipient.Text.Trim());
                var current = runtime.Secrets.Read() with { SendGrid = config };
                if (config.Enabled && !NotificationProviders.Configured(ReminderChannel.Email, current)) { emailStatus.Text = "Vérifiez la clé et les deux adresses email."; return; }
                if (runtime.Secrets.Read().SendGrid != secrets.SendGrid) { emailStatus.Text = "Configuration modifiée ailleurs. Rouvrez les réglages."; return; }
                commands.SaveSecrets(current); secrets = secrets with { SendGrid = current.SendGrid }; emailStatus.Text = "Enregistré localement et chiffré. Aucun envoi effectué.";
            }
            catch { emailStatus.Text = "Impossible d’enregistrer les identifiants."; }
        };
        email.Children.Add(saveEmail); email.Children.Add(emailStatus);
        email.Children.Add(TestButton("Envoyer un email de test", ReminderChannel.Email, runtime, demo, email));
        var removeEmail = Button("Supprimer la clé SendGrid"); removeEmail.IsEnabled = !demo;
        removeEmail.Click += (_, _) => { try { commands.SaveSecrets(runtime.Secrets.Read() with { SendGrid = new() }); secrets = secrets with { SendGrid = new() }; emailKey.Clear(); from.Clear(); recipient.Clear(); emailEnabled.IsChecked = false; emailStatus.Text = "Identifiants supprimés ; canal désactivé."; } catch { emailStatus.Text = "Suppression impossible."; } };
        email.Children.Add(removeEmail); email.Children.Add(Link("Vérifier l’expéditeur ↗", "https://www.twilio.com/docs/sendgrid/for-developers/sending-email/sender-identity"));
        email.Children.Add(Link("Tarifs SendGrid ↗", "https://www.twilio.com/en-us/products/email-api/pricing"));
        root.Children.Add(new Expander { Header = "SendGrid · Email", Content = email, Margin = new Thickness(0, 4, 0, 10) });

        var limits = Panel(); var policy = preferences.Current.PhonePolicy;
        var smsLimit = Field(limits, "Maximum de SMS par jour (0–100)", policy.SmsPerDay.ToString());
        var callLimit = Field(limits, "Maximum d’appels par jour (0–20)", policy.CallsPerDay.ToString());
        var quiet = Check("Heures silencieuses pour SMS et appels", policy.QuietEnabled); limits.Children.Add(quiet);
        var start = Field(limits, "Début (heure, 0–23)", policy.QuietStart.ToString()); var end = Field(limits, "Fin (heure, 0–23)", policy.QuietEnd.ToString());
        limits.Children.Add(Ui.Text("Fuseau horaire", 11, "MutedBrush"));
        var zones = TimeZoneInfo.GetSystemTimeZones(); var zoneId = ReminderPlanner.Zone(policy).Id;
        if (!zones.Any(z => z.Id == zoneId) && TimeZoneInfo.TryConvertIanaIdToWindowsId(zoneId, out var windowsId)) zoneId = windowsId;
        var zone = new ComboBox { ItemsSource = zones, DisplayMemberPath = "DisplayName", SelectedValuePath = "Id", SelectedValue = zoneId, Tag = System.Windows.Application.Current.FindResource("DropdownClockIcon"), Margin = new Thickness(0, 5, 0, 12) }; limits.Children.Add(zone);
        var limitStatus = Hint(""); var saveLimits = Button("Enregistrer les limites");
        saveLimits.Click += (_, _) =>
        {
            if (!int.TryParse(smsLimit.Text, out var sm) || sm < 0 || sm > 100 || !int.TryParse(callLimit.Text, out var ca) || ca < 0 || ca > 20 || !int.TryParse(start.Text, out var st) || st < 0 || st > 23 || !int.TryParse(end.Text, out var en) || en < 0 || en > 23 || zone.SelectedValue is not string tz)
            { limitStatus.Text = "Vérifiez les limites, les heures et le fuseau."; return; }
            try { if (preferences.Current.PhonePolicy != policy) { limitStatus.Text = "Limites modifiées ailleurs. Rouvrez les réglages."; return; } commands.SavePreferences(p => p with { PhonePolicy = new(sm, ca, quiet.IsChecked == true, st, en, tz) }); policy = preferences.Current.PhonePolicy; limitStatus.Text = "Limites enregistrées. Les tests téléphoniques les respectent aussi."; }
            catch { limitStatus.Text = "Enregistrement impossible."; }
        };
        limits.Children.Add(saveLimits); limits.Children.Add(limitStatus);
        root.Children.Add(new Expander { Header = "Limites et heures silencieuses", Content = limits, Margin = new Thickness(0, 4, 0, 10) });
        return root;
    }
    private static TextBox Field(Panel panel, string label, string value)
    {
        panel.Children.Add(Ui.Text(label, 11, "MutedBrush"));
        var box = new TextBox { Text = value, Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(9, 7, 9, 7), MinHeight = 34 };
        System.Windows.Automation.AutomationProperties.SetName(box, label); panel.Children.Add(box); return box;
    }
    private static PasswordBox Password(Panel panel, string label, string value)
    {
        panel.Children.Add(Ui.Text(label, 11, "MutedBrush"));
        var box = new PasswordBox { Password = value, Margin = new Thickness(0, 5, 0, 12), Padding = new Thickness(9, 7, 9, 7), MinHeight = 34 };
        box.SetResourceReference(Control.BackgroundProperty, "PanelBrush"); box.SetResourceReference(Control.ForegroundProperty, "TextBrush"); box.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        System.Windows.Automation.AutomationProperties.SetName(box, label); panel.Children.Add(box); return box;
    }
    private static Button Link(string title, string url)
    {
        var button = Button(title); button.Style = (Style)System.Windows.Application.Current.FindResource("QuietButton");
        button.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }; return button;
    }
    private static Button TestButton(string label, ReminderChannel channel, ReminderRuntime runtime, bool demo, Panel panel)
    {
        var button = Button(label); button.IsEnabled = !demo; var status = Hint(""); status.Visibility = Visibility.Collapsed; panel.Children.Add(status);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            status.Visibility = Visibility.Visible;
            try { status.Text = (await runtime.TestAsync(channel)).Detail; }
            catch { status.Text = "Test impossible ; vérifiez la configuration et le journal local."; }
            finally { button.IsEnabled = !demo; }
        }; return button;
    }
    internal static FrameworkElement History(ReminderRuntime runtime)
    {
        var root = Panel(); var rows = Panel();
        void Render()
        {
            rows.Children.Clear();
            if (runtime.Error is not null) rows.Children.Add(Hint(runtime.Error));
            var entries = runtime.History.Where(d => d.UpdatedAt > DateTimeOffset.UtcNow.AddDays(-30)).OrderByDescending(d => d.UpdatedAt).ToArray();
            if (entries.Length == 0) rows.Children.Add(Hint("Aucun rappel envoyé. Les événements des 30 derniers jours apparaîtront ici."));
            foreach (var item in entries)
            {
                var panel = Panel(); var title = Ui.Text($"{ChannelName(item.Occurrence.Channel)} · {ReminderPlanner.Label(item.Occurrence.Kind)}", 13); title.FontWeight = FontWeights.SemiBold; panel.Children.Add(title);
                panel.Children.Add(Hint($"{item.Occurrence.AccountName}\n{item.UpdatedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss zzz}\n{item.Detail}"));
                root.ToolTip = "Les notifications acceptées ne sont pas nécessairement lues ou livrées.";
                var border = new Border { Child = panel, BorderThickness = new Thickness(0, 0, 0, 1) }; border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); rows.Children.Add(border);
            }
        }
        var refresh = Button("Actualiser l’historique"); refresh.Click += (_, _) => Render(); root.Children.Add(refresh); root.Children.Add(rows);
        root.IsVisibleChanged += (_, _) => { if (root.IsVisible) Render(); }; Render(); return root;
    }
}
