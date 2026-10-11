using System.Globalization;
using System.Windows.Shapes;

namespace CodexTracker.App;

internal sealed class ManualCodexResetWindow : ThemedWindow
{
    private readonly MainWindow _owner;
    private readonly string _revision;
    private sealed record DayChoice(DateTime Day, string Label) { public override string ToString() => Label; }
    private readonly ComboBox _date;
    private readonly TextBox _time;
    private readonly TextBlock _status;
    private readonly TextBlock _summary = new() { FontSize = 13, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _affected = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Button _save;

    internal ManualCodexResetWindow(MainWindow owner) : base(owner, Loc.T("Déclarer un reset général Codex"), owner.Theme, 580, 575)
    {
        _owner = owner; _revision = owner.Commands.Revision;
        var declared = owner.Preferences.Current.ManualCodexReset;

        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); heading.ColumnDefinitions.Add(new());
        var badge = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top, Child = Ui.Icon("ResetClockIcon", 20, "AccentBrush", 2) };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush"); heading.Children.Add(badge);
        var intro = new StackPanel(); Grid.SetColumn(intro, 1);
        var question = Ui.Text(Loc.T("Quand le reset a-t-il eu lieu ?"), 18); question.FontWeight = FontWeights.Medium; intro.Children.Add(question);
        var lead = Ui.Text(Loc.T("OpenAI a remis à zéro les quotas de tout le monde ? Indiquez l’heure : vos comptes Codex afficheront 100 % jusqu’à leur prochain relevé."), 12, "MutedBrush");
        lead.Margin = new Thickness(0, 5, 0, 0); intro.Children.Add(lead); heading.Children.Add(intro); Body.Children.Add(heading);

        // Most declarations follow an announcement read minutes ago: one click fills the date and time.
        var quick = new WrapPanel { Margin = new Thickness(0, 18, 0, 4) };
        foreach (var (label, minutes) in new[] { (Loc.T("À l’instant"), 0), (Loc.T("Il y a 30 min"), 30), (Loc.T("Il y a 1 h"), 60), (Loc.T("Il y a 2 h"), 120), (Loc.T("Il y a 4 h"), 240) })
        {
            var choice = new Button { Content = label, Padding = new Thickness(11, 5, 11, 5), Margin = new Thickness(0, 0, 6, 6), FontSize = 12 };
            choice.Click += (_, _) => SetLocal(PreviewClock.UtcNow.AddMinutes(-minutes).ToLocalTime().DateTime);
            quick.Children.Add(choice);
        }
        Body.Children.Add(quick);

        var at = (declared?.At ?? PreviewClock.UtcNow).ToLocalTime();
        var fields = new StackPanel();
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        var date = new StackPanel(); date.Children.Add(Ui.Text(Loc.T("Date"), 11, "MutedBrush"));
        // A declaration lasts at most a week: the last eight days cover every useful date.
        var today = PreviewClock.UtcNow.LocalDateTime.Date;
        var days = Enumerable.Range(0, 8).Select(offset => today.AddDays(-offset)).Append(at.Date).Distinct().Where(d => d <= today).OrderDescending()
            .Select(d => new DayChoice(d, d == today ? Loc.F("Aujourd’hui · {0:ddd d MMM}", d) : d == today.AddDays(-1) ? Loc.F("Hier · {0:ddd d MMM}", d) : Capitalized(d.ToString("dddd d MMMM", Loc.Culture)))).ToArray();
        _date = new ComboBox { ItemsSource = days, DisplayMemberPath = "Label", Tag = FindResource("DropdownCalendarIcon"), Margin = new Thickness(0, 6, 12, 0) };
        _date.SelectedItem = days.FirstOrDefault(d => d.Day == at.Date) ?? days[0];
        System.Windows.Automation.AutomationProperties.SetName(_date, Loc.T("Date du reset Codex")); date.Children.Add(_date); row.Children.Add(date);
        var time = new StackPanel(); time.Children.Add(Ui.Text(Loc.T("Heure (HH:mm)"), 11, "MutedBrush"));
        _time = new TextBox { Text = at.ToString("HH:mm", CultureInfo.InvariantCulture), MaxLength = 5, Margin = new Thickness(0, 6, 0, 0) };
        // The day list follows the time field's height so both fields line up.
        _date.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(ActualHeight)) { Source = _time });
        System.Windows.Automation.AutomationProperties.SetName(_time, Loc.T("Heure du reset Codex")); time.Children.Add(_time); Grid.SetColumn(time, 1); row.Children.Add(time);
        fields.Children.Add(row);
        _summary.Margin = new Thickness(0, 14, 0, 0); _summary.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); fields.Children.Add(_summary);
        var zone = Ui.Text(Loc.F("Heure locale · {0}", TimeZoneInfo.Local.DisplayName), 11, "MutedBrush"); zone.Margin = new Thickness(0, 3, 0, 0); fields.Children.Add(zone);
        Body.Children.Add(Ui.Panel(fields));

        var changes = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        changes.Children.Add(Ui.Text(Loc.T("Ce qui change"), 11, "MutedBrush"));
        changes.Children.Add(Point(_affected, "GoodBrush"));
        changes.Children.Add(Point(Ui.Text(Loc.T("Claude Code, les resets en réserve et l’historique mesuré sont conservés."), 12), "SubtleBrush"));
        changes.Children.Add(Point(Ui.Text(Loc.T("Un nouveau relevé remplace la déclaration pour son compte. Elle expire après 7 jours (5 heures pour le quota court)."), 12), "SubtleBrush"));
        Body.Children.Add(changes);
        _status = Ui.Text("", 11, "DangerBrush"); _status.Margin = new Thickness(0, 10, 0, 0); Body.Children.Add(_status);

        var buttons = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (declared is not null)
        {
            var clear = new Button { Content = Loc.T("Annuler la déclaration"), Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 0, 8, 0) };
            clear.SetResourceReference(ForegroundProperty, "DangerBrush");
            clear.Click += (_, _) => Save(null); buttons.Children.Add(clear);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; Grid.SetColumn(actions, 1);
        var cancel = new Button { Content = Loc.T("Fermer"), IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => Close(); actions.Children.Add(cancel);
        _save = new Button { Content = Loc.T("Déclarer le reset"), IsDefault = true, Style = (Style)FindResource("PrimaryButton") };
        _save.Click += (_, _) => { if (TryRead(out var instant)) Save(instant); }; actions.Children.Add(_save);
        buttons.Children.Add(actions); Body.Children.Add(buttons);
        _date.SelectionChanged += (_, _) => Validate(); _time.TextChanged += (_, _) => Validate();
        Loaded += (_, _) => _time.Focus(); Validate();
    }

    private static Grid Point(TextBlock text, string brush)
    {
        var line = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); line.ColumnDefinitions.Add(new ColumnDefinition());
        var dot = new Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(1, 6, 0, 0) };
        dot.SetResourceReference(Shape.FillProperty, brush); line.Children.Add(dot);
        Grid.SetColumn(text, 1); line.Children.Add(text); return line;
    }
    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Loc.Culture) + text[1..];
    private void SetLocal(DateTime local)
    {
        _date.SelectedItem = _date.Items.OfType<DayChoice>().FirstOrDefault(d => d.Day == local.Date) ?? _date.SelectedItem;
        _time.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
    private bool TryRead(out DateTimeOffset instant)
    {
        instant = default;
        if ((_date.SelectedItem as DayChoice)?.Day is not { } day || !TimeOnly.TryParseExact(_time.Text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        { _status.Text = Loc.T("Indiquez une date et une heure au format HH:mm."); return false; }
        var local = DateTime.SpecifyKind(day.Date.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local))
        { _status.Text = Loc.T("Cette heure tombe dans un changement d’heure. Choisissez une heure locale sans ambiguïté."); return false; }
        instant = new(local, TimeZoneInfo.Local.GetUtcOffset(local));
        if (instant > PreviewClock.UtcNow) { _status.Text = Loc.T("Le reset doit avoir eu lieu : cette heure est dans le futur."); return false; }
        _status.Text = ""; return true;
    }
    private void Validate()
    {
        var valid = TryRead(out var instant);
        _save.IsEnabled = valid;
        if (!valid) { _summary.Text = ""; _affected.Text = ""; return; }
        var local = instant.ToLocalTime();
        var ago = PreviewClock.UtcNow - instant;
        var relative = ago.TotalMinutes < 1 ? Loc.T("à l’instant") : ago.TotalHours < 1 ? Loc.F("il y a {0} min", (int)ago.TotalMinutes)
            : ago.TotalDays < 1 ? Loc.F("il y a {0} h {1:00}", (int)ago.TotalHours, ago.Minutes) : Loc.F("il y a {0} j", (int)ago.TotalDays);
        var day = local.ToString("dddd d MMMM", Loc.Culture);
        _summary.Text = Loc.F("{0} à {1:HH:mm} · {2}", Capitalized(day), local, relative);
        // Same rule as the declaration itself: only Codex accounts without a newer reading change.
        var count = _owner.TrackerService.State.Accounts.Count(a => a.Profile.Provider == AccountProvider.Codex && !(a.Snapshot?.FetchedAt >= instant));
        _affected.Text = count switch
        {
            0 => Loc.T("Aucun compte Codex sans relevé plus récent : rien ne changera à l’affichage."),
            1 => Loc.T("1 compte Codex passe à 100 % sur la semaine et les 5 heures."),
            _ => Loc.F("{0} comptes Codex passent à 100 % sur la semaine et les 5 heures.", count)
        };
    }
    private void Save(DateTimeOffset? at)
    {
        try { _owner.Commands.DeclareCodexReset(at, PreviewClock.UtcNow, _revision); Close(); }
        catch (InvalidOperationException) { _status.Text = Loc.T("Les réglages ont changé. Fermez puis rouvrez ce formulaire avant de déclarer le reset."); _save.IsEnabled = false; }
        catch (ArgumentException error) { _status.Text = error.Message; }
        catch (Exception) { _status.Text = Loc.T("La déclaration n’a pas pu être enregistrée. Réessayez."); }
    }
}
