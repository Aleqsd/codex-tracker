using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace CodexTracker.App;

internal sealed class CalendarWindow : ThemedWindow
{
    private static readonly Uri GoogleImport = new("https://calendar.google.com/calendar/r/settings/export");
    private readonly ITrackerService _service;
    private readonly PreferencesStore _preferences;
    private readonly Guid? _accountId;
    private readonly Action<Uri> _openBrowser;
    private readonly TextBlock _summary, _status, _preparedSummary;
    private readonly Button _export, _google;
    private readonly Border _ready;
    private readonly TextBox _path;
    private readonly StackPanel _events = new();
    private IReadOnlyList<CalendarEntry> _displayed = [];
    private string? _preparedFile;
    private IReadOnlyList<CalendarEntry> _preparedEntries = [];
    private bool _closed;
    private IReadOnlyList<CalendarEntry> Entries() => CalendarExport.Entries(
        _accountId is { } id ? _service.State with { Accounts = _service.State.Accounts.Where(a => a.Profile.Id == id).ToArray() } : _service.State,
        p => PrivacyText.Account(p, _service.State, _preferences.Current), DateTimeOffset.UtcNow);

    public CalendarWindow(Window owner, ITrackerService service, PreferencesStore preferences, ThemeManager theme, Guid? accountId = null, Action<Uri>? openBrowser = null)
        : base(owner, "Calendrier", theme, 580, 600)
    {
        _service = service; _preferences = preferences; _accountId = accountId;
        _openBrowser = openBrowser ?? (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));
        _status = Ui.Text("", 11); _status.Margin = new Thickness(0, 12, 0, 0);
        var heading = new StackPanel { Orientation = Orientation.Horizontal }; heading.Children.Add(CalendarIcon("TextBrush", 23));
        var title = Ui.Text("Google Agenda", 20); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(11, 0, 0, 0); heading.Children.Add(title); Body.Children.Add(heading);
        _summary = Ui.Text("", 12, "MutedBrush"); _summary.Margin = new Thickness(0, 10, 0, 20); Body.Children.Add(_summary);
        var import = new StackPanel();
        var importTitle = Ui.Text("Toutes vos échéances", 14); importTitle.FontWeight = FontWeights.SemiBold; import.Children.Add(importTitle);
        var hint = Ui.Text("Resets et expirations, dans l’agenda de votre choix.", 12, "MutedBrush"); hint.Margin = new Thickness(0, 6, 0, 16); import.Children.Add(hint);
        var buttonContent = new StackPanel { Orientation = Orientation.Horizontal }; buttonContent.Children.Add(CalendarIcon("PrimaryButtonTextBrush", 16)); buttonContent.Children.Add(new TextBlock { Text = "Importer dans Google Agenda", Margin = new Thickness(9, 0, 10, 0) }); buttonContent.Children.Add(new TextBlock { Text = "↗" });
        _google = new Button { Content = buttonContent, Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 10, 14, 10) };
        System.Windows.Automation.AutomationProperties.SetName(_google, "Importer dans Google Agenda");
        _google.ToolTip = "Utilise votre navigateur habituel. Si Google demande une connexion, connectez-vous puis cliquez à nouveau ici.";
        _google.Click += (_, _) => { try { PrepareGoogleImport(); Launch(GoogleImport, "Google Agenda est ouvert. Sélectionnez le fichier préparé puis validez l’import."); } catch (Exception) { _status.Text = "Impossible de préparer le fichier. Essayez l’export vers un autre emplacement."; } }; import.Children.Add(_google);
        var note = Ui.Text("Le fichier est préparé ici ; vous confirmez l’import dans Google Agenda.", 11, "MutedBrush"); note.Margin = new Thickness(0, 10, 0, 0); import.Children.Add(note); Body.Children.Add(Ui.Panel(import));
        var steps = new StackPanel();
        _preparedSummary = Ui.Text("", 12); _preparedSummary.FontWeight = FontWeights.SemiBold; steps.Children.Add(_preparedSummary);
        var instructions = Ui.Text("1. Dans Google, cliquez sur « Sélectionner un fichier » et collez ce chemin.\n2. Choisissez votre agenda, puis cliquez sur « Importer ».", 11, "MutedBrush"); instructions.Margin = new Thickness(0, 8, 0, 10); steps.Children.Add(instructions);
        _path = new TextBox { IsReadOnly = true, FontSize = 11, Padding = new Thickness(8), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(_path, "Chemin du fichier à importer"); steps.Children.Add(_path);
        var actions = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        var copy = new Button { Content = "Copier le chemin", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(-10, 0, 8, 0) };
        copy.Click += (_, _) => { try { System.Windows.Clipboard.SetText(_path.Text); _status.Text = "Chemin copié. Collez-le dans le sélecteur de fichier de Google Agenda."; } catch (Exception) { _status.Text = "Sélectionnez le chemin ci-dessus et copiez-le avec Ctrl+C."; } }; actions.Children.Add(copy);
        var reopen = new Button { Content = "Ouvrir Google Agenda ↗", Style = (Style)FindResource("QuietButton") }; reopen.Click += (_, _) => Launch(GoogleImport, "Terminez l’import dans Google Agenda."); actions.Children.Add(reopen); steps.Children.Add(actions);
        _ready = Ui.Panel(steps); _ready.Margin = new Thickness(0, 12, 0, 0); _ready.Visibility = Visibility.Collapsed; Body.Children.Add(_ready);
        var single = new StackPanel();
        var directHint = Ui.Text("Ouvre un événement prérempli. Vérifiez-le, puis cliquez sur Enregistrer dans Google Agenda.", 11, "MutedBrush"); directHint.Margin = new Thickness(0, 12, 0, 8); single.Children.Add(directHint);
        single.Children.Add(new ScrollViewer { Content = _events, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Body.Children.Add(new Expander { Header = "Ajouter une seule échéance", Content = single, Margin = new Thickness(0, 18, 0, 12) });
        _export = new Button { Content = "Exporter un fichier .ics…", Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-10, 0, 0, 12), ToolTip = "Pour Google Agenda, Outlook, Apple Calendar ou une sauvegarde locale" }; _export.Click += (_, _) => Export(); Body.Children.Add(_export);
        Body.Children.Add(Ui.Text("Ajout ponctuel, sans synchronisation. Évitez d’ajouter la même échéance plusieurs fois.", 11, "MutedBrush")); Body.Children.Add(_status);
        service.Changed += Changed; Closed += (_, _) => { _closed = true; service.Changed -= Changed; }; Update();
    }
    private UIElement CalendarIcon(string brush, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var path = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("DropdownCalendarIcon"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush); canvas.Children.Add(path);
        return new Viewbox { Child = canvas, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Update);
    private void Update()
    {
        if (_closed) return;
        var entries = Entries(); _export.IsEnabled = entries.Count > 0; _google.IsEnabled = entries.Count > 0;
        _summary.Text = entries.Count == 0 ? "Aucune échéance future disponible. Actualisez le compte dans Codex." : $"{entries.Count} échéances connues · prochaine le {Display.Exact(entries[0].StartsAt)}";
        _summary.ToolTip = entries.Count == 0 ? null : Display.Zone(entries[0].StartsAt);
        if (_displayed.SequenceEqual(entries)) return;
        _displayed = entries; _events.Children.Clear();
        foreach (var entry in entries)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 8) }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new StackPanel { Margin = new Thickness(0, 0, 10, 0) }; label.Children.Add(Ui.Text(entry.Title, 12));
            var date = Ui.Text(Display.Exact(entry.StartsAt), 11, "MutedBrush"); date.Margin = new Thickness(0, 4, 0, 0); date.ToolTip = Display.Zone(entry.StartsAt); label.Children.Add(date); row.Children.Add(label);
            var add = new Button { Content = "Ajouter ↗", Tag = entry, Style = (Style)FindResource("QuietButton"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Préremplir cet événement dans Google Agenda" }; add.Click += (_, _) => OpenEntry(entry); Grid.SetColumn(add, 1); row.Children.Add(add); _events.Children.Add(row);
        }
    }
    internal void OpenEntry(CalendarEntry entry)
    {
        if (!Entries().Any(e => e == entry)) { Update(); _status.Text = "Cette échéance a changé. Choisissez sa nouvelle date dans la liste."; return; }
        Launch(CalendarExport.GoogleEventLink(entry), "Événement prérempli ouvert. Cliquez sur Enregistrer dans Google Agenda pour l’ajouter.");
    }
    private void Launch(Uri uri, string success)
    {
        try { _openBrowser(uri); _status.Text = success; }
        catch (Exception) { _status.Text = "Le navigateur n’a pas pu s’ouvrir. Réessayez ou utilisez le fichier .ics."; }
    }
    internal string PrepareGoogleImport()
    {
        if (_preparedFile is not null && File.Exists(_preparedFile) && _preparedEntries.SequenceEqual(Entries())) return _preparedFile;
        var directory = Path.Combine(_preferences.DataDirectory, "calendar-exports"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"CodexTracker-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.ics"); ExportToFile(path); return path;
    }
    private void Export()
    {
        if (Entries().Count == 0) { Update(); return; }
        var dialog = new SaveFileDialog { Filter = "Calendrier iCalendar|*.ics", FileName = "CodexTracker-echeances.ics", DefaultExt = ".ics", AddExtension = true }; if (dialog.ShowDialog(this) != true) return;
        try { ExportToFile(dialog.FileName); } catch (Exception) { _status.Text = "Le fichier n’a pas pu être enregistré. Choisissez un autre emplacement."; }
    }
    internal void ExportToFile(string path)
    {
        var entries = Entries(); if (entries.Count == 0) throw new InvalidOperationException("Aucune échéance à exporter.");
        File.WriteAllText(path, CalendarExport.Serialize(entries, DateTimeOffset.UtcNow), new UTF8Encoding(false));
        _preparedEntries = entries; _preparedFile = Path.GetFullPath(path); _path.Text = _preparedFile; _preparedSummary.Text = $"Fichier prêt · {entries.Count} échéances · {DateTime.Now:HH:mm:ss}";
        _ready.Visibility = Visibility.Visible; _google.IsEnabled = true; _status.Text = "Fichier prêt à importer. Aucun ajout dans Google Agenda n’est confirmé par le tracker.";
    }
}
