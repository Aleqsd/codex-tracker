using System.Diagnostics;
using Microsoft.Win32;
using CodexTracker.Codex;

namespace CodexTracker.App;

/// <summary>Settings → Calendar: the Google connection used to add each reset credit with reminders.</summary>
internal sealed class GoogleCalendarSettingsView : StackPanel
{
    private static readonly Uri Console = new("https://console.cloud.google.com/apis/credentials");
    private static readonly Uri Consent = new("https://console.cloud.google.com/auth/audience");
    private readonly GoogleCalendarSync _calendar;
    private readonly TextBlock _status = Ui.Text("", 13), _detail = Ui.Text("", 11, "MutedBrush"), _error = Ui.Text("", 11, "WarningBrush");
    private readonly WrapPanel _actions = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _steps = new();
    private bool _busy;
    private string? _actionError;

    internal GoogleCalendarSettingsView(GoogleCalendarSync calendar)
    {
        _calendar = calendar;
        _status.FontWeight = FontWeights.Medium; Children.Add(_status);
        _detail.Margin = new Thickness(0, 5, 0, 0); Children.Add(_detail);
        Children.Add(_steps); Children.Add(_actions);
        _error.Margin = new Thickness(0, 8, 0, 0); Children.Add(_error);
        Loaded += (_, _) => { _calendar.Changed += Changed; Render(); };
        Unloaded += (_, _) => _calendar.Changed -= Changed;
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Render);

    private void Render()
    {
        _actions.Children.Clear(); _steps.Children.Clear();
        var error = _actionError ?? _calendar.Error;
        _error.Text = error ?? ""; _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (_calendar.IsConnected)
        {
            _status.Text = _calendar.Email is { } email ? Loc.F("Connecté · {0}", email) : Loc.T("Connecté");
            _detail.Text = Loc.T("Chaque reset en réserve reçoit un événement à son expiration, avec rappels Google 3 jours, 1 jour et 1 heure avant. Accès limité aux événements de votre agenda principal.");
            Add(Loc.T("Déconnecter"), async () => await _calendar.DisconnectAsync(), quiet: true);
            return;
        }
        // Three steps in their real order; each says whether it is done.
        var available = _calendar.IsAvailable;
        _status.Text = !available ? Loc.T("Google Agenda indisponible") : _calendar.IsConnecting ? Loc.T("Connexion en cours dans le navigateur…") : Loc.T("Non connecté");
        _detail.Text = !available ? Loc.T("Disponible dans le tracker installé, pas dans la démonstration.")
            : Loc.T("Une configuration unique avec votre propre client Google. Accès limité aux événements de votre agenda principal.");
        var client = _calendar.HasClient;
        Step(1, client, Loc.T("Importer le client OAuth"), client ? Loc.T("Client de type « Application de bureau » importé.")
            : Loc.T("Google Cloud : activez l’API Google Calendar, créez un client « Application de bureau », téléchargez son JSON."),
            client ? (Loc.T("Changer…"), Import, false) : (Loc.T("Importer…"), Import, true), client ? null : (Loc.T("Ouvrir Google Cloud ↗"), Console), enabled: available);
        Step(2, null, Loc.T("Publier l’écran de consentement"), Loc.T("Statut « En production » : sinon Google retire l’accès au bout de 7 jours."),
            null, (Loc.T("Ouvrir ↗"), Consent));
        Step(3, false, Loc.T("Autoriser l’accès aux événements"), Loc.T("S’ouvre dans votre navigateur. Google peut signaler une application non validée : c’est votre propre client."),
            (Loc.T("Connecter"), () => _calendar.ConnectAsync(), true), null, enabled: available && client && !_calendar.IsConnecting);
        if (client && available) Add(Loc.T("Supprimer le client OAuth"), () => { _calendar.ForgetClient(); return Task.CompletedTask; }, quiet: true);
    }

    /// <param name="done">True: completed. False: still to do. Null: done outside the tracker, cannot be checked.</param>
    private void Step(int number, bool? done, string title, string detail, (string Text, Func<Task> Run, bool Primary)? action,
        (string Text, Uri Target)? link, bool enabled = true)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
        var mark = new TextBlock { Text = done == true ? "✓" : number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (done == true) { badge.SetResourceReference(Border.BackgroundProperty, "GoodSoftBrush"); mark.SetResourceReference(TextBlock.ForegroundProperty, "GoodBrush"); }
        else if (done == false && enabled) { badge.SetResourceReference(Border.BackgroundProperty, "PrimaryButtonBrush"); mark.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryButtonTextBrush"); }
        else { badge.BorderThickness = new Thickness(1); badge.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); mark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); }
        badge.Child = mark; row.Children.Add(badge);
        var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var name = Ui.Text(title, 12); name.FontWeight = FontWeights.Medium; text.Children.Add(name);
        var hint = Ui.Text(detail, 11, "MutedBrush"); hint.Margin = new Thickness(0, 3, 0, 0); text.Children.Add(hint);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        // The help link sits under the explanation it completes; the right column keeps the single action.
        if (link is { } target)
        {
            var open = new Button { Content = target.Text, Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 3, 0, 0), FontSize = 11 };
            open.Click += (_, _) => Process.Start(new ProcessStartInfo(target.Target.AbsoluteUri) { UseShellExecute = true });
            text.Children.Add(open);
        }
        if (action is { } run)
        {
            var button = new Button { Content = run.Text, IsEnabled = enabled, Padding = new Thickness(12, 6, 12, 6) };
            if (run.Primary) button.Style = (Style)FindResource("PrimaryButton");
            Wire(button, run.Run); buttons.Children.Add(button);
        }
        Grid.SetColumn(buttons, 2); row.Children.Add(buttons);
        _steps.Children.Add(row);
    }

    private Task Import()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Client OAuth Google (*.json)|*.json"), CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        if (new FileInfo(dialog.FileName).Length > 65_536) throw new ArgumentException(Loc.T("Fichier client OAuth illisible."));
        _calendar.ImportClient(File.ReadAllText(dialog.FileName));
        return Task.CompletedTask;
    }

    private Button Add(string text, Func<Task> action, bool quiet = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(quiet ? -10 : 0, 0, quiet ? 0 : 10, 6), HorizontalAlignment = HorizontalAlignment.Left };
        if (quiet) button.Style = (Style)FindResource("QuietButton");
        Wire(button, action);
        _actions.Children.Add(button); return button;
    }

    private void Wire(Button button, Func<Task> action)
    {
        button.Click += async (_, _) =>
        {
            if (_busy) return;
            _busy = true; button.IsEnabled = false; _actionError = null;
            try { await action(); }
            catch (Exception ex) when (ex is GoogleCalendarException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
            { _actionError = ex is System.Net.Sockets.SocketException ? Loc.T("Impossible d’ouvrir le retour local de connexion. Réessayez.") : ex.Message; }
            catch (OperationCanceledException) { }
            finally { _busy = false; button.IsEnabled = true; }
            Render();
        };
    }
}
