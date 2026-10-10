using System.Globalization;

namespace CodexTracker.App;

internal sealed class ManualCodexResetWindow : ThemedWindow
{
    private readonly MainWindow _owner;
    private readonly string _revision;
    private readonly DatePicker _date;
    private readonly TextBox _time;
    private readonly TextBlock _status;
    private readonly Button _save;

    internal ManualCodexResetWindow(MainWindow owner) : base(owner, Loc.T("Déclarer un reset Codex"), owner.Theme, 565, 490)
    {
        _owner = owner; _revision = owner.Commands.Revision;
        Body.Children.Add(Ui.Text(Loc.T("À quelle heure le reset général a-t-il eu lieu ?"), 16));
        var explanation = Ui.Text(Loc.T("Tous les comptes Codex sans relevé plus récent afficheront 100 % sur les quotas de 5 heures et de la semaine, avec la mention « reset déclaré ». Les comptes Claude Code et les resets en réserve sont conservés."), 12, "MutedBrush");
        explanation.Margin = new Thickness(0, 10, 0, 16); Body.Children.Add(explanation);
        var at = (owner.Preferences.Current.ManualCodexReset?.At ?? PreviewClock.UtcNow).ToLocalTime();
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        var date = new StackPanel(); date.Children.Add(Ui.Text(Loc.T("Date"), 11, "MutedBrush"));
        _date = new DatePicker { SelectedDate = at.Date, DisplayDateEnd = PreviewClock.UtcNow.LocalDateTime.Date, Margin = new Thickness(0, 6, 12, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_date, Loc.T("Date du reset Codex")); date.Children.Add(_date); row.Children.Add(date);
        var time = new StackPanel(); time.Children.Add(Ui.Text(Loc.T("Heure (HH:mm)"), 11, "MutedBrush"));
        _time = new TextBox { Text = at.ToString("HH:mm", CultureInfo.InvariantCulture), MaxLength = 5, Margin = new Thickness(0, 6, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(_time, Loc.T("Heure du reset Codex")); time.Children.Add(_time); Grid.SetColumn(time, 1); row.Children.Add(time); Body.Children.Add(row);
        var zone = Ui.Text(Loc.F("Heure locale · {0}", TimeZoneInfo.Local.DisplayName), 11, "MutedBrush"); zone.Margin = new Thickness(0, 8, 0, 0); Body.Children.Add(zone);
        var note = Ui.Text(Loc.T("Les derniers relevés et l’historique mesuré restent conservés. Un nouveau relevé remplace la déclaration pour son compte. Les prochaines échéances restent inconnues jusqu’à ce relevé ; la déclaration expire après 5 h / 7 jours."), 11, "MutedBrush");
        note.Margin = new Thickness(0, 16, 0, 0); Body.Children.Add(note);
        _status = Ui.Text("", 11, "DangerBrush"); _status.Margin = new Thickness(0, 9, 0, 0); Body.Children.Add(_status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (owner.Preferences.Current.ManualCodexReset is not null)
        {
            var clear = new Button { Content = Loc.T("Annuler la déclaration"), Margin = new Thickness(0, 0, 8, 8) };
            clear.Click += (_, _) => Save(null); buttons.Children.Add(clear);
        }
        var cancel = new Button { Content = Loc.T("Fermer"), IsCancel = true, Margin = new Thickness(0, 0, 8, 8) }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        _save = new Button { Content = Loc.T("Déclarer le reset"), IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 0, 8) };
        _save.Click += (_, _) => { if (TryRead(out var instant)) Save(instant); }; buttons.Children.Add(_save); Body.Children.Add(buttons);
        _date.SelectedDateChanged += (_, _) => Validate(); _time.TextChanged += (_, _) => Validate();
        Loaded += (_, _) => _time.Focus(); Validate();
    }

    private bool TryRead(out DateTimeOffset instant)
    {
        instant = default;
        if (_date.SelectedDate is not { } day || !TimeOnly.TryParseExact(_time.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        { _status.Text = Loc.T("Indiquez une date et une heure au format HH:mm."); return false; }
        var local = DateTime.SpecifyKind(day.Date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
        { _status.Text = Loc.T("Cette heure tombe dans un changement d’heure. Choisissez une heure locale sans ambiguïté."); return false; }
        instant = new(local, TimeZoneInfo.Local.GetUtcOffset(local));
        if (instant > PreviewClock.UtcNow) { _status.Text = Loc.T("Le reset doit avoir eu lieu : cette heure est dans le futur."); return false; }
        _status.Text = ""; return true;
    }
    private void Validate() => _save.IsEnabled = TryRead(out _);
    private void Save(DateTimeOffset? at)
    {
        try { _owner.Commands.DeclareCodexReset(at, PreviewClock.UtcNow, _revision); Close(); }
        catch (InvalidOperationException) { _status.Text = Loc.T("Les réglages ont changé. Fermez puis rouvrez ce formulaire avant de déclarer le reset."); _save.IsEnabled = false; }
        catch (ArgumentException error) { _status.Text = error.Message; }
        catch (Exception) { _status.Text = Loc.T("La déclaration n’a pas pu être enregistrée. Réessayez."); }
    }
}
