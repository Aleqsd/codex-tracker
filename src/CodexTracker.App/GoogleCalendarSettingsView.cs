using System.Diagnostics;
using Microsoft.Win32;
using CodexTracker.Codex;

namespace CodexTracker.App;

/// <summary>Settings → Calendar: the Google connection used to add each reset credit with reminders.</summary>
internal sealed class GoogleCalendarSettingsView : StackPanel
{
    private static readonly Uri Console = new("https://console.cloud.google.com/apis/credentials");
    private readonly GoogleCalendarSync _calendar;
    private readonly TextBlock _status = Ui.Text("", 13), _detail = Ui.Text("", 11, "MutedBrush"), _error = Ui.Text("", 11, "WarningBrush");
    private readonly WrapPanel _actions = new() { Margin = new Thickness(0, 12, 0, 0) };
    private bool _busy;
    private string? _actionError;

    internal GoogleCalendarSettingsView(GoogleCalendarSync calendar)
    {
        _calendar = calendar;
        _status.FontWeight = FontWeights.Medium; Children.Add(_status);
        _detail.Margin = new Thickness(0, 5, 0, 0); Children.Add(_detail);
        Children.Add(_actions);
        _error.Margin = new Thickness(0, 8, 0, 0); Children.Add(_error);
        Loaded += (_, _) => { _calendar.Changed += Changed; Render(); };
        Unloaded += (_, _) => _calendar.Changed -= Changed;
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Render);

    private void Render()
    {
        _actions.Children.Clear();
        var error = _actionError ?? _calendar.Error;
        _error.Text = error ?? ""; _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (!_calendar.IsAvailable)
        {
            _status.Text = Loc.T("Google Agenda indisponible"); _detail.Text = Loc.T("Disponible dans le tracker installé, pas dans la démonstration.");
            return;
        }
        if (_calendar.IsConnected)
        {
            _status.Text = _calendar.Email is { } email ? Loc.F("Connecté · {0}", email) : Loc.T("Connecté");
            _detail.Text = Loc.T("Chaque reset en réserve reçoit un événement à son expiration, avec rappels Google 3 jours, 1 jour et 1 heure avant. Accès limité aux événements de votre agenda principal.");
            Add(Loc.T("Déconnecter"), async () => await _calendar.DisconnectAsync(), quiet: true);
            return;
        }
        _status.Text = _calendar.IsConnecting ? Loc.T("Connexion en cours dans le navigateur…") : Loc.T("Non connecté");
        if (!_calendar.HasClient)
        {
            _detail.Text = Loc.T("Une fois : dans Google Cloud, activez l’API Google Calendar, créez un client OAuth de type « Application de bureau », puis importez son fichier JSON ici. Publiez l’écran de consentement (« En production ») pour que l’accès n’expire pas après 7 jours.");
            Add(Loc.T("Importer le client OAuth…"), Import);
            Add(Loc.T("Ouvrir Google Cloud ↗"), () => { Process.Start(new ProcessStartInfo(Console.AbsoluteUri) { UseShellExecute = true }); return Task.CompletedTask; }, quiet: true);
            return;
        }
        _detail.Text = Loc.T("Client OAuth importé. La connexion s’ouvre dans votre navigateur ; Google peut signaler une application non validée : c’est votre propre client.");
        var connect = Add(Loc.T("Connecter Google Agenda"), () => _calendar.ConnectAsync());
        connect.IsEnabled = !_calendar.IsConnecting;
        Add(Loc.T("Changer de client OAuth…"), Import, quiet: true);
        Add(Loc.T("Supprimer le client OAuth"), () => { _calendar.ForgetClient(); return Task.CompletedTask; }, quiet: true);
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
        _actions.Children.Add(button); return button;
    }
}
