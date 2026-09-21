using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace CodexTracker.App;

internal sealed class CalendarWindow : ThemedWindow
{
    private readonly ITrackerService _service;
    private readonly PreferencesStore _preferences;
    private readonly Guid? _accountId;
    private readonly TextBlock _summary, _status;
    private readonly Button _export, _google;
    private bool _closed;
    private IReadOnlyList<CalendarEntry> Entries() => CalendarExport.Entries(
        _accountId is { } id ? _service.State with { Accounts = _service.State.Accounts.Where(a => a.Profile.Id == id).ToArray() } : _service.State,
        p => PrivacyText.Account(p, _service.State, _preferences.Current), DateTimeOffset.UtcNow);

    public CalendarWindow(Window owner, ITrackerService service, PreferencesStore preferences, ThemeManager theme, Guid? accountId = null)
        : base(owner, "Exporter vers un calendrier", theme, 530, 510)
    {
        _service = service; _preferences = preferences; _accountId = accountId;
        _status = Ui.Text("", 11); _status.Margin = new Thickness(0, 15, 0, 0);
        Body.Children.Add(Ui.Text("Les prochaines échéances connues", 16));
        _summary = Ui.Text("", 12, "MutedBrush"); _summary.Margin = new Thickness(0, 12, 0, 20); Body.Children.Add(_summary);
        Body.Children.Add(Ui.Text("Le fichier reprend les noms de vos comptes et les dates connues : resets semaine et 5 heures, expirations des réserves.", 12, "MutedBrush"));
        _export = new Button { Content = "1. Enregistrer le fichier .ics", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 10) };
        _export.Click += (_, _) => Export(); Body.Children.Add(_export);
        _google = new Button { Content = "2. Ouvrir l’import Google Calendar ↗", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
        _google.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://calendar.google.com/calendar/u/0/r/settings/export") { UseShellExecute = true }); }
            catch (Exception) { _status.Text = "Ouvrez Google Calendar → Paramètres → Importer et exporter."; }
        }; Body.Children.Add(_google);
        var guide = Ui.Text("Dans Google Calendar : sélectionnez le fichier .ics, choisissez le calendrier de destination, puis cliquez sur Importer. Cette étape se fait dans votre navigateur.", 11, "MutedBrush"); guide.Margin = new Thickness(0, 14, 0, 12); Body.Children.Add(guide);
        Body.Children.Add(Ui.Text("Import ponctuel, sans synchronisation. Les dates restent prévisionnelles jusqu’à confirmation par Codex. Évitez de réimporter plusieurs fois le même fichier.", 11, "MutedBrush"));
        Body.Children.Add(_status);
        service.Changed += Changed; Closed += (_, _) => { _closed = true; service.Changed -= Changed; }; Update();
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Update);
    private void Update()
    {
        if (_closed) return;
        var entries = Entries(); _export.IsEnabled = entries.Count > 0;
        _summary.Text = entries.Count == 0 ? "Aucune échéance future disponible. Actualisez le compte dans Codex." :
            $"{entries.Count} échéances exportables · prochaine le {Display.Exact(entries[0].StartsAt)}\n{Display.Zone(entries[0].StartsAt)}";
    }
    private void Export()
    {
        var entries = Entries(); if (entries.Count == 0) { Update(); return; }
        var dialog = new SaveFileDialog { Filter = "Calendrier iCalendar|*.ics", FileName = "CodexTracker-echeances.ics", DefaultExt = ".ics", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ExportToFile(dialog.FileName);
        }
        catch (Exception) { _status.Text = "Le fichier n’a pas pu être enregistré. Choisissez un autre emplacement."; }
    }
    internal void ExportToFile(string path)
    {
        var entries = Entries();
        if (entries.Count == 0) throw new InvalidOperationException("Aucune échéance à exporter.");
        File.WriteAllText(path, CalendarExport.Serialize(entries, DateTimeOffset.UtcNow), new UTF8Encoding(false));
        _status.Text = $"{entries.Count} échéances enregistrées. Le fichier est prêt à importer."; _google.IsEnabled = true;
    }
}
