using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;
using CodexTracker.Codex;

namespace CodexTracker.App;

/// <summary>
/// Settings → Google Agenda: what the connection brings first, then its state. Connected, the synchronized credits are
/// listed; otherwise one primary action: connect, use the downloaded client file, or follow four linked setup steps.
/// </summary>
internal sealed class GoogleCalendarSettingsView : StackPanel
{
    private static readonly Uri NewProject = new("https://console.cloud.google.com/projectcreate");
    private static readonly Uri CalendarApi = new("https://console.cloud.google.com/apis/library/calendar-json.googleapis.com");
    private static readonly Uri Consent = new("https://console.cloud.google.com/auth/overview");
    private static readonly Uri NewClient = new("https://console.cloud.google.com/auth/clients/create");
    private readonly GoogleCalendarSync _calendar;
    private readonly Func<IReadOnlyList<CalendarEntry>> _credits;
    private readonly Border _state = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _stateText = new() { FontSize = 11, FontWeight = FontWeights.Medium };
    private readonly StackPanel _body = new();
    private readonly TextBlock _error = Ui.Text("", 11, "WarningBrush");
    private readonly WrapPanel _errorActions = new();
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(3) };
    private string? _detectedFile;
    private bool _busy;
    private string? _actionError;

    /// <param name="credits">Reset credits still held, as calendar entries: the ones the calendar should contain.</param>
    internal GoogleCalendarSettingsView(GoogleCalendarSync calendar, Func<IReadOnlyList<CalendarEntry>> credits)
    {
        _calendar = calendar; _credits = credits;
        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); hero.ColumnDefinitions.Add(new ColumnDefinition()); hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        badge.Child = Glyph("DropdownCalendarIcon", "AccentBrush", 20); hero.Children.Add(badge);
        var intro = new StackPanel();
        var title = Ui.Text(Loc.T("Chaque reset en réserve dans votre agenda"), 15); title.FontWeight = FontWeights.Medium; intro.Children.Add(title);
        var promise = Ui.Text(Loc.T("Un événement à sa date d’expiration, avec des rappels Google 3 jours, 1 jour et 1 heure avant : plus aucun reset perdu."), 12, "MutedBrush");
        promise.Margin = new Thickness(0, 4, 0, 0); intro.Children.Add(promise);
        Grid.SetColumn(intro, 1); hero.Children.Add(intro);
        _state.Child = _stateText; _state.Margin = new Thickness(12, 2, 0, 0); _state.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_state, 2); hero.Children.Add(_state);
        Children.Add(hero);
        _body.Margin = new Thickness(54, 14, 0, 0); Children.Add(_body);
        var problem = new StackPanel { Margin = new Thickness(54, 10, 0, 0) };
        problem.Children.Add(_error); _errorActions.Margin = new Thickness(0, 4, 0, 0); problem.Children.Add(_errorActions);
        Children.Add(problem);
        _watch.Tick += (_, _) => Watch();
        Loaded += (_, _) => { _calendar.Changed += Changed; Render(); _watch.Start(); };
        Unloaded += (_, _) => { _calendar.Changed -= Changed; _watch.Stop(); };
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Render);

    /// <summary>While no client is imported, a client file appearing in Downloads offers itself without a file picker.</summary>
    private void Watch()
    {
        if (_busy || _calendar.IsConnected || _calendar.IsConnecting || !_calendar.IsAvailable) return;
        var found = _calendar.FindClientFile()?.Path;
        if (found != _detectedFile) Render();
    }

    private void Render()
    {
        _body.Children.Clear(); _errorActions.Children.Clear();
        var error = _actionError ?? _calendar.Error;
        _error.Text = error ?? ""; _error.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        if (_calendar.Problem == GoogleCalendarProblem.ApiDisabled)
            Link(_errorActions, Loc.T("Activer l’API Google Calendar ↗"), _calendar.ProjectId is { } project ? new Uri(CalendarApi.AbsoluteUri + "?project=" + project) : CalendarApi);
        if (_calendar.IsConnected) { Connected(); return; }
        var available = _calendar.IsAvailable;
        State(_calendar.IsConnecting ? Loc.T("Connexion…") : available || _calendar.DemoSetup ? Loc.T("Non connecté") : Loc.T("Indisponible"), _calendar.IsConnecting ? "AccentSoftBrush" : "RaisedBrush", _calendar.IsConnecting ? "AccentBrush" : "MutedBrush");
        if (!available && !_calendar.DemoSetup)
        {
            _body.Children.Add(Ui.Text(Loc.T("Disponible dans le tracker installé, pas dans la démonstration."), 11, "MutedBrush"));
            return;
        }
        if (_calendar.IsConnecting)
        {
            _body.Children.Add(Ui.Text(Loc.T("Terminez l’autorisation dans votre navigateur. Google peut signaler une application non validée : c’est votre propre client (Paramètres avancés → Accéder à Codex Tracker)."), 12, "MutedBrush"));
            return;
        }
        if (_calendar.HasClient)
        {
            var actions = Row();
            Button(actions, Loc.T("Connecter Google Agenda"), () => _calendar.ConnectAsync(), primary: true);
            Button(actions, Loc.T("Changer de client…"), Import);
            Button(actions, Loc.T("Supprimer le client"), () => { _calendar.ForgetClient(); return Task.CompletedTask; });
            Hint(Loc.T("S’ouvre dans votre navigateur : choisissez votre compte et autorisez l’accès aux événements."));
            return;
        }
        _detectedFile = _calendar.FindClientFile()?.Path;
        if (_detectedFile is { } file)
        {
            // The file downloaded from Google Cloud is the last step before the authorization: one click.
            var found = Ui.Text(Loc.F("Client Google trouvé dans vos téléchargements : {0}", Path.GetFileName(file)), 12);
            found.TextTrimming = TextTrimming.CharacterEllipsis; found.TextWrapping = TextWrapping.NoWrap; found.ToolTip = file; _body.Children.Add(found);
            var actions = Row(); actions.Margin = new Thickness(0, 10, 0, 0);
            Button(actions, Loc.T("Utiliser et connecter"), () => _calendar.UseClientFileAsync(file), primary: true);
            Button(actions, Loc.T("Choisir un autre fichier…"), Import);
            return;
        }
        var intro = Ui.Text(Loc.T("Une configuration unique, avec votre propre client Google (5 minutes environ) :"), 12);
        intro.Margin = new Thickness(0, 0, 0, 4); _body.Children.Add(intro);
        Step(1, Loc.T("Créer un projet Google Cloud"), null, (Loc.T("Créer ↗"), NewProject));
        Step(2, Loc.T("Activer l’API Google Calendar"), null, (Loc.T("Activer ↗"), CalendarApi));
        Step(3, Loc.T("Configurer l’écran de consentement (externe), puis le publier « En production »"), Loc.T("Sinon Google retire l’accès au bout de 7 jours."), (Loc.T("Configurer ↗"), Consent));
        Step(4, Loc.T("Créer un client « Application de bureau » et télécharger son JSON"), Loc.T("Le fichier est détecté ici dès son téléchargement."), (Loc.T("Créer ↗"), NewClient));
        var other = Row(); other.Margin = new Thickness(0, 12, 0, 0);
        Button(other, Loc.T("Choisir le fichier…"), Import);
    }

    private void Connected()
    {
        State(Loc.T("Connecté"), "GoodSoftBrush", "GoodBrush");
        var account = Ui.Text(_calendar.Email is { } email ? Loc.F("Agenda principal de {0}", email) : Loc.T("Agenda principal"), 12, "MutedBrush");
        _body.Children.Add(account);
        var credits = _credits().OrderBy(c => c.StartsAt).ToArray();
        var linked = credits.Where(c => _calendar.Link(CalendarExport.Key(c)) is { Status: CalendarLinkStatus.Confirmed }).ToArray();
        var heading = Ui.Text(credits.Length == 0 ? Loc.T("Aucun reset en réserve pour le moment.")
            : Loc.F("{0} sur {1} resets en réserve dans l’agenda", linked.Length, credits.Length), 12);
        heading.FontWeight = FontWeights.Medium; heading.Margin = new Thickness(0, 14, 0, 6); _body.Children.Add(heading);
        foreach (var credit in credits.Take(6))
        {
            var link = _calendar.Link(CalendarExport.Key(credit));
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var confirmed = link is { Status: CalendarLinkStatus.Confirmed };
            var mark = Ui.Text(confirmed ? "✓" : "·", 12, confirmed ? "GoodBrush" : "SubtleBrush"); mark.FontWeight = FontWeights.Medium; row.Children.Add(mark);
            var name = credit.Title.Split(" · ")[0];
            var text = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run(name));
            text.Inlines.Add(new System.Windows.Documents.Run("  " + Loc.F("expire {0}", Display.ResetHour(credit.StartsAt).ToLowerInvariant())) { Foreground = ThemeManager.GetBrush("MutedBrush") });
            text.ToolTip = $"{Display.Exact(credit.StartsAt)} · {Display.Zone(credit.StartsAt)}";
            Grid.SetColumn(text, 1); row.Children.Add(text);
            if (link?.HtmlLink is { } url)
            {
                var open = new Button { Content = Loc.T("Ouvrir ↗"), Style = (Style)FindResource("LinkButton"), FontSize = 11, Padding = new Thickness(6, 0, 0, 0) };
                open.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Grid.SetColumn(open, 2); row.Children.Add(open);
            }
            else if (!confirmed)
            {
                var state = Ui.Text(link?.Status == CalendarLinkStatus.Deleted ? Loc.T("supprimé dans Google") : Loc.T("en attente"), 11, "MutedBrush");
                Grid.SetColumn(state, 2); row.Children.Add(state);
            }
            _body.Children.Add(row);
        }
        if (credits.Length > 6) Hint(Loc.F("et {0} autres, dans Resets.", credits.Length - 6));
        var actions = Row(); actions.Margin = new Thickness(0, 14, 0, 0);
        var disconnect = Button(actions, Loc.T("Déconnecter"), async () => await _calendar.DisconnectAsync());
        disconnect.IsEnabled = _calendar.IsAvailable;
        disconnect.ToolTip = Loc.T("Révoque l’accès du tracker ; les événements déjà créés restent dans Google Agenda.");
    }

    private void State(string text, string background, string foreground)
    {
        _stateText.Text = text; _state.SetResourceReference(Border.BackgroundProperty, background); _stateText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
    }
    private WrapPanel Row() { var row = new WrapPanel(); _body.Children.Add(row); return row; }
    private void Hint(string text) { var hint = Ui.Text(text, 11, "MutedBrush"); hint.Margin = new Thickness(0, 6, 0, 0); _body.Children.Add(hint); }
    private static UIElement Glyph(string geometry, string brush, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var path = new System.Windows.Shapes.Path { Data = (Geometry)Application.Current.FindResource(geometry), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush); canvas.Children.Add(path);
        return new Viewbox { Child = canvas, Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>A setup step: its number, what to do, and the Google Cloud page where it is done.</summary>
    private void Step(int number, string title, string? detail, (string Text, Uri Target) link)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        badge.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var mark = new TextBlock { Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 10, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); badge.Child = mark; row.Children.Add(badge);
        var text = new StackPanel { Margin = new Thickness(0, 1, 12, 0) };
        text.Children.Add(Ui.Text(title, 12));
        if (detail is not null) { var hint = Ui.Text(detail, 11, "MutedBrush"); hint.Margin = new Thickness(0, 2, 0, 0); text.Children.Add(hint); }
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var open = new Button { Content = link.Text, Style = (Style)FindResource("LinkButton"), FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(0, 0, 0, 0) };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(link.Target.AbsoluteUri) { UseShellExecute = true });
        Grid.SetColumn(open, 2); row.Children.Add(open);
        _body.Children.Add(row);
    }
    private static void Link(Panel target, string text, Uri uri)
    {
        var open = new Button { Content = text, FontSize = 11, Padding = new Thickness(0, 2, 0, 0) };
        open.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        open.Click += (_, _) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        target.Children.Add(open);
    }

    private Task Import()
    {
        var dialog = new OpenFileDialog { Filter = Loc.T("Client OAuth Google (*.json)|*.json"), CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
        // Choosing a file means wanting the connection: it starts right away.
        return _calendar.UseClientFileAsync(dialog.FileName);
    }

    private Button Button(Panel target, string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, primary ? "PrimaryButton" : "QuietButton");
        if (!primary) button.Margin = new Thickness(-4, 0, 8, 6);
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
        target.Children.Add(button); return button;
    }
}
