namespace CodexTracker.App;

internal sealed record ResetAccountChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

internal sealed class ResetsView : UserControl
{
    private readonly PreferencesStore _preferences;
    private readonly ComboBox _accounts;
    private readonly StackPanel _timeline = new();
    private readonly TextBlock _summary = Ui.Text("", 12, "MutedBrush");
    private readonly TextBlock _reserves = Ui.Text("", 11, "MutedBrush");
    private readonly Button _refresh;
    private readonly Grid _heading;
    private readonly List<(ResetScheduleEntry Entry, TextBlock Countdown, TextBlock Freshness)> _rows = [];
    private TrackerState _state = new([], null);
    private DateTimeOffset? _nextBoundary;
    private bool _syncing;
    private ResetKind? _kindFilter;
    private bool _weekView;
    private DateOnly _week = ResetCalendar.Monday(DateOnly.FromDateTime(PreviewClock.UtcNow.LocalDateTime));
    private DateOnly _renderedDate;
    private readonly RadioButton _weekChoice;

    internal ResetsView(PreferencesStore preferences, Action<Guid?> calendar, Action refresh)
    {
        _preferences = preferences;
        var root = new Grid { Margin = new Thickness(0, 15, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var heading = _heading = new Grid();
        var title = Ui.Text("Calendrier des resets", 20); title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title);
        var export = new Button { Content = "Google Agenda ↗", Style = (Style)FindResource("QuietButton"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Options Google Agenda pour le compte sélectionné, tous les types de resets" };
        var introduction = new StackPanel(); introduction.Children.Add(heading); _summary.Margin = new Thickness(0, 7, 0, 5); introduction.Children.Add(_summary); introduction.Children.Add(_reserves); root.Children.Add(introduction);

        var filters = new Grid { Margin = new Thickness(0, 14, 0, 10) }; filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) }); filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _accounts = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id", Tag = FindResource("AccountsIcon") };
        export.Click += (_, _) => calendar((_accounts.SelectedItem as ResetAccountChoice)?.Id);
        System.Windows.Automation.AutomationProperties.SetName(_accounts, "Filtrer les resets par compte");
        _accounts.SelectionChanged += (_, _) => { if (!_syncing) Render(); }; filters.Children.Add(_accounts);
        _refresh = new Button { Content = "Actualiser", Style = (Style)FindResource("QuietButton"), ToolTip = "Actualise le compte actif dans Codex ; les autres conservent leur dernier relevé" };
        _refresh.Click += (_, _) => refresh();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var agenda = new RadioButton { Content = "Agenda", GroupName = "ResetView", IsChecked = true, Style = (Style)FindResource("ResetKindFilter") };
        _weekChoice = new RadioButton { Content = "Semaine", GroupName = "ResetView", Style = (Style)FindResource("ResetKindFilter") };
        agenda.Checked += (_, _) => { _weekView = false; Render(); };
        _weekChoice.Checked += (_, _) => { _weekView = true; Render(); };
        System.Windows.Automation.AutomationProperties.SetName(agenda, "Vue agenda des resets");
        System.Windows.Automation.AutomationProperties.SetName(_weekChoice, "Vue semaine des resets");
        actions.Children.Add(agenda); actions.Children.Add(_weekChoice); _refresh.Margin = new Thickness(8, 0, 0, 0); actions.Children.Add(_refresh);
        Grid.SetColumn(actions, 2); filters.Children.Add(actions); Grid.SetRow(filters, 1); root.Children.Add(filters);
        var kinds = new WrapPanel { Margin = new Thickness(0, 0, 0, 3) };
        foreach (var (kind, label) in new (ResetKind?, string)[] { (null, "Tous"), (ResetKind.Weekly, "Hebdomadaires"), (ResetKind.Short, "5 heures"), (ResetKind.Reserve, "Réserves") })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (kind is { } value) { var icon = KindIcon(value, 14); icon.Margin = new Thickness(0, 0, 7, 0); content.Children.Add(icon); }
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var choice = new RadioButton { Content = content, Tag = kind, GroupName = "ResetKinds", IsChecked = kind is null, Style = (Style)FindResource("ResetKindFilter"), Margin = new Thickness(0, 0, 4, 0) };
            System.Windows.Automation.AutomationProperties.SetName(choice, label);
            choice.Checked += (_, _) => { _kindFilter = kind; Render(); };
            kinds.Children.Add(choice);
        }
        var types = new Grid(); types.ColumnDefinitions.Add(new ColumnDefinition()); types.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        types.Children.Add(kinds); Grid.SetColumn(export, 1); types.Children.Add(export);
        Grid.SetRow(types, 2); root.Children.Add(types);
        var scroll = new ScrollViewer { Content = _timeline, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 3); root.Children.Add(scroll); Content = root;
    }
    internal void Update(TrackerState state)
    {
        _state = state; _refresh.IsEnabled = !state.IsBusy;
        var selected = (_accounts.SelectedItem as ResetAccountChoice)?.Id;
        var choices = new[] { new ResetAccountChoice(null, "Tous les comptes") }
            .Concat(state.Accounts.Select(a => new ResetAccountChoice(a.Profile.Id, AccountName(a)))).ToArray();
        if (_accounts.ItemsSource is not ResetAccountChoice[] previous || !previous.SequenceEqual(choices))
        {
            _syncing = true; _accounts.ItemsSource = choices;
            _accounts.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0]; _syncing = false;
        }
        Render();
    }
    private string AccountName(AccountState account) => PrivacyText.Account(account.Profile, _state, _preferences.Current);
    internal void ShowWeek() => _weekChoice.IsChecked = true;
    private void Render()
    {
        var accountId = (_accounts.SelectedItem as ResetAccountChoice)?.Id;
        var accounts = _state.Accounts.Where(a => accountId is null || a.Profile.Id == accountId).ToArray();
        var entries = ResetSchedule.Entries(_state, accountId).Where(e => _kindFilter is null || e.Kind == _kindFilter).ToArray();
        var now = PreviewClock.UtcNow;
        _renderedDate = DateOnly.FromDateTime(now.LocalDateTime);
        var future = entries.Where(e => e.At > now).ToArray();
        var reached = entries.Where(e => e.At <= now).OrderByDescending(e => e.At).ToArray();
        var unknown = entries.Where(e => e.At is null).ToArray();
        _summary.Text = accounts.Length == 0 ? "Les dates apparaîtront dès qu’un compte sera détecté dans Codex." : $"{future.Length} à venir" + (unknown.Length > 0 ? " · certaines dates ne sont pas communiquées" : "");
        var counts = accounts.Select(a => a.Snapshot?.AvailableResetCredits).ToArray();
        _reserves.Text = counts.Length == 0 ? "" : counts.All(n => n is null) ? "Réserves : non communiquées" : $"Réserves : {counts.Sum(n => (long)(n ?? 0))} resets au dernier relevé" + (counts.Any(n => n is null) ? $" · {counts.Count(n => n is null)} comptes sans compteur" : "");
        _reserves.ToolTip = "Les comptes inactifs ne sont pas actualisés en arrière-plan. Le compteur serveur fait foi ; les dates détaillées ne permettent pas de déduire le nombre de resets disponibles.";
        _heading.Visibility = _weekView ? Visibility.Collapsed : Visibility.Visible;
        _reserves.Visibility = _weekView ? Visibility.Collapsed : Visibility.Visible;
        _summary.ToolTip = _reserves.Text + "\n" + _reserves.ToolTip;
        _rows.Clear(); _timeline.Children.Clear();
        if (_kindFilter is null or ResetKind.Reserve) AddPriority(accountId, accounts, now);
        if (_weekView) AddWeek(entries, now);
        else
        {
            foreach (var day in future.GroupBy(e => e.At!.Value.ToLocalTime().Date))
                AddGroup($"À venir · {day.Key:dddd dd MMMM yyyy}", day.ToArray());
            AddGroup("Dates atteintes · à vérifier", reached);
        }
        AddGroup("Dates non communiquées", unknown, showCount: false);
        if (entries.Length == 0) { var empty = Ui.Text(_kindFilter == ResetKind.Reserve ? "Aucune réserve à afficher pour cette sélection." : "Aucune échéance à afficher pour cette sélection.", 13, "MutedBrush"); empty.Margin = new Thickness(0, 24, 0, 0); _timeline.Children.Add(empty); }
        _nextBoundary = future.FirstOrDefault()?.At;
        UpdateTimes(now);
    }
    private void AddPriority(Guid? accountId, AccountState[] accounts, DateTimeOffset now)
    {
        var entry = ResetCalendar.PriorityReserve(_state, now, accountId);
        if (entry is null)
        {
            if (accounts.Any(a => a.Snapshot?.AvailableResetCredits > 0))
            {
                var unavailable = Ui.Text("Priorité indisponible · aucune expiration future connue.", 11, "MutedBrush");
                unavailable.Margin = new Thickness(0, 10, 0, 4); _timeline.Children.Add(unavailable);
            }
            return;
        }
        var panel = new Grid(); panel.ColumnDefinitions.Add(new ColumnDefinition()); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identityPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; panel.Children.Add(identityPanel);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = KindIcon(ResetKind.Reserve, 16); icon.Margin = new Thickness(0, 0, 8, 0); titleRow.Children.Add(icon);
        var title = Ui.Text("Réserve prioritaire", 12); title.FontWeight = FontWeights.SemiBold;
        titleRow.Children.Add(title); identityPanel.Children.Add(titleRow);
        var identity = Ui.Text(_weekView ? AccountName(entry.Account) : $"{AccountName(entry.Account)} · {entry.CreditTitle ?? "Crédit de reset"}", 12);
        identity.TextWrapping = TextWrapping.NoWrap; identity.TextTrimming = TextTrimming.CharacterEllipsis;
        identity.Margin = new Thickness(0, _weekView ? 4 : 7, 0, 0); identityPanel.Children.Add(identity);
        var timing = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(timing, 1); panel.Children.Add(timing);
        timing.Children.Add(Ui.Text($"{entry.At!.Value.ToLocalTime():dd/MM/yyyy HH:mm:ss}", 11, "MutedBrush"));
        var countdown = Ui.Text("", 11); var freshness = Ui.Text("", 11, "MutedBrush");
        countdown.TextAlignment = TextAlignment.Right; timing.Children.Add(countdown); identityPanel.Children.Add(freshness);
        if (_weekView && entry.Account.IsActiveInCodex && entry.Account.Error is null && now - entry.Account.Snapshot!.FetchedAt < TimeSpan.FromMinutes(5))
            freshness.Visibility = Visibility.Collapsed;
        var border = new Border { Child = panel, Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 5) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.ToolTip = $"Première expiration future connue, selon le dernier relevé ; disponibilité à vérifier dans Codex.\nLes dates manquantes peuvent masquer une expiration plus proche.\n{EntryHint(entry)}";
        _rows.Add((entry, countdown, freshness)); _timeline.Children.Add(border);
    }
    private void AddWeek(ResetScheduleEntry[] entries, DateTimeOffset now)
    {
        var navigation = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition()); navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var range = Ui.Text($"{_week:dd MMM} – {_week.AddDays(6):dd MMM yyyy}", 13); range.FontWeight = FontWeights.SemiBold;
        range.VerticalAlignment = VerticalAlignment.Center; navigation.Children.Add(range);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, name, shift) in new[] { ("‹", "Semaine précédente", -7), ("Aujourd’hui", "Semaine actuelle", 0), ("›", "Semaine suivante", 7) })
        {
            var button = new Button { Content = label, ToolTip = name, Style = (Style)FindResource("QuietButton"), Padding = new Thickness(9, 5, 9, 5) };
            System.Windows.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => { _week = shift == 0 ? ResetCalendar.Monday(DateOnly.FromDateTime(PreviewClock.UtcNow.LocalDateTime)) : _week.AddDays(shift); Render(); };
            buttons.Children.Add(button);
        }
        Grid.SetColumn(buttons, 1); navigation.Children.Add(buttons); _timeline.Children.Add(navigation);
        var grid = new Grid();
        var days = ResetCalendar.Week(entries, _week, TimeZoneInfo.Local);
        for (var index = 0; index < days.Count; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var day = days[index]; var content = new StackPanel();
            var isToday = day.Date == DateOnly.FromDateTime(now.LocalDateTime);
            var dayName = Ui.Text(day.Date.ToString("ddd"), 11, "MutedBrush"); dayName.TextAlignment = TextAlignment.Center; content.Children.Add(dayName);
            var number = Ui.Text(day.Date.ToString("dd"), 17); number.FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal;
            number.TextAlignment = TextAlignment.Center; number.Margin = new Thickness(0, 2, 0, 5); content.Children.Add(number);
            foreach (var entry in day.Entries)
            {
                var card = new StackPanel();
                var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                var icon = KindIcon(entry.Kind, 12); icon.Margin = new Thickness(0, 0, 4, 0); typeRow.Children.Add(icon);
                var kind = Ui.Text(entry.Kind switch { ResetKind.Weekly => "Hebdo", ResetKind.Short => "5 h", _ => "Réserve" }, 10); kind.FontWeight = FontWeights.SemiBold; typeRow.Children.Add(kind); card.Children.Add(typeRow);
                var account = Ui.Text(AccountName(entry.Account), 11); account.TextWrapping = TextWrapping.NoWrap; account.TextTrimming = TextTrimming.CharacterEllipsis; card.Children.Add(account);
                card.Children.Add(Ui.Text(entry.At!.Value.ToLocalTime().ToString("HH:mm:ss"), 11));
                var countdown = Ui.Text("", 10, "MutedBrush"); countdown.Margin = new Thickness(0, 4, 0, 0);
                var freshness = Ui.Text("", 10, "MutedBrush"); freshness.Margin = new Thickness(0, 4, 0, 0);
                freshness.TextWrapping = TextWrapping.NoWrap; freshness.TextTrimming = TextTrimming.CharacterEllipsis;
                card.Children.Add(countdown); card.Children.Add(freshness); _rows.Add((entry, countdown, freshness));
                var box = new Border { Child = card, Padding = new Thickness(7, 10, 7, 10), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 6), ToolTip = EntryHint(entry) };
                box.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); content.Children.Add(box);
            }
            if (day.Entries.Count == 0) { var empty = Ui.Text("—", 12, "MutedBrush"); empty.TextAlignment = TextAlignment.Center; content.Children.Add(empty); }
            var column = new Border { Child = content, Padding = new Thickness(3), BorderThickness = new Thickness(0, isToday ? 2 : 0, 0, 0) };
            column.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
            System.Windows.Automation.AutomationProperties.SetName(column, $"Échéances du {day.Date:dd/MM/yyyy}");
            Grid.SetColumn(column, index); grid.Children.Add(column);
        }
        _timeline.Children.Add(grid);
        var count = days.Sum(d => d.Entries.Count);
        var note = Ui.Text($"{count} échéance{(count == 1 ? "" : "s")} cette semaine · {TimeZoneInfo.Local.DisplayName}. Les dates atteintes restent à vérifier dans Codex.", 11, "MutedBrush");
        note.Margin = new Thickness(0, 8, 0, 4); _timeline.Children.Add(note);
    }
    private string EntryHint(ResetScheduleEntry entry) =>
        $"{entry.Kind switch { ResetKind.Weekly => "Reset hebdomadaire", ResetKind.Short => "Reset 5 heures", _ => "Expiration de réserve" }}\n{AccountName(entry.Account)}\n{entry.CreditTitle}\n{Display.Exact(entry.At)}\n{(entry.At is { } at ? Display.Zone(at) : "Date non communiquée")}\nRelevé : {Display.Exact(entry.Account.Snapshot?.FetchedAt)}\n{entry.Account.Error}";
    private void AddGroup(string title, IReadOnlyList<ResetScheduleEntry> entries, bool showCount = true)
    {
        if (entries.Count == 0) return;
        var heading = Ui.Text(showCount ? $"{title}  ·  {entries.Count}" : title, 12, "MutedBrush"); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 13, 0, 7); _timeline.Children.Add(heading);
        foreach (var entry in entries)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(177) });
            var date = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            var day = Ui.Text(entry.At?.ToLocalTime().ToString("dd") ?? "—", 24); day.FontWeight = FontWeights.SemiBold; date.Children.Add(day);
            date.Children.Add(Ui.Text(entry.At?.ToLocalTime().ToString("MMM") ?? "", 11, "MutedBrush"));
            date.Children.Add(Ui.Text(entry.At?.ToLocalTime().ToString("yyyy") ?? "", 10, "MutedBrush"));
            var marker = new Border { Child = date, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 15, 0), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Stretch };
            marker.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); row.Children.Add(marker);
            var identity = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var kind = entry.Kind switch { ResetKind.Weekly => "Reset hebdomadaire", ResetKind.Short => "Reset 5 heures", _ => "Expiration de réserve" };
            if (entry.IsUndetailedReserve) kind = "Réserves sans date";
            var detail = Ui.Text(kind, 13); detail.FontWeight = FontWeights.SemiBold; var typeRow = new StackPanel { Orientation = Orientation.Horizontal };
            var typeIcon = KindIcon(entry.Kind, 16); typeIcon.Margin = new Thickness(0, 0, 7, 0); typeRow.Children.Add(typeIcon); typeRow.Children.Add(detail); identity.Children.Add(typeRow);
            if (!string.IsNullOrWhiteSpace(entry.CreditTitle))
            {
                var creditTitle = Ui.Text(entry.CreditTitle, 11, "MutedBrush"); creditTitle.Margin = new Thickness(0, 3, 0, 0); creditTitle.TextWrapping = TextWrapping.NoWrap; creditTitle.TextTrimming = TextTrimming.CharacterEllipsis; creditTitle.ToolTip = entry.CreditTitle; identity.Children.Add(creditTitle);
            }
            var account = Ui.Text(AccountName(entry.Account), 12); account.Margin = new Thickness(0, 4, 0, 0); account.TextWrapping = TextWrapping.NoWrap; account.TextTrimming = TextTrimming.CharacterEllipsis; account.ToolTip = entry.Account.Profile.Email; identity.Children.Add(account);
            var freshness = Ui.Text("", 11, "MutedBrush"); freshness.Margin = new Thickness(0, 4, 0, 0); identity.Children.Add(freshness);
            if (entry.GrantedAt is { } granted) { var receipt = Ui.Text($"Reçu le {Display.Exact(granted)}", 11, "MutedBrush"); receipt.Margin = new Thickness(0, 4, 0, 0); receipt.ToolTip = Display.Zone(granted); identity.Children.Add(receipt); }
            Grid.SetColumn(identity, 1); row.Children.Add(identity);
            var timing = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            var exact = Ui.Text(entry.At is null ? "Date indisponible" : entry.At.Value.ToLocalTime().ToString("HH:mm:ss"), 13); exact.FontWeight = FontWeights.SemiBold; exact.TextAlignment = TextAlignment.Right; timing.Children.Add(exact);
            if (entry.At is { } at) { var clock = Ui.Text($"UTC{at.ToLocalTime():zzz}", 11, "MutedBrush"); clock.Margin = new Thickness(0, 4, 0, 0); clock.TextAlignment = TextAlignment.Right; clock.ToolTip = $"{Display.Exact(at)}\n{Display.Zone(at)}"; timing.Children.Add(clock); }
            var countdown = Ui.Text("", 11, "MutedBrush"); countdown.Margin = new Thickness(0, 4, 0, 0); countdown.TextAlignment = TextAlignment.Right; timing.Children.Add(countdown);
            Grid.SetColumn(timing, 2); row.Children.Add(timing);
            var border = new Border { Child = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12, 7, 13) }; border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            border.ToolTip = entry.Account.Snapshot is { } snapshot ? $"Dernier relevé : {Display.Exact(snapshot.FetchedAt)}\n{Display.Zone(snapshot.FetchedAt)}\n{Display.ReserveSummary(snapshot)}\n{entry.CreditTitle}\n{entry.Account.Error}" : "Aucun relevé disponible pour ce compte.";
            _timeline.Children.Add(border); _rows.Add((entry, countdown, freshness));
        }
    }
    private FrameworkElement KindIcon(ResetKind kind, double size)
    {
        var key = kind switch { ResetKind.Weekly => "ResetTimerIcon", ResetKind.Short => "ResetRepeatIcon", _ => "ResetTicketIcon" };
        var path = new System.Windows.Shapes.Path { Data = (Geometry)FindResource(key), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }
    internal void Tick()
    {
        var now = PreviewClock.UtcNow;
        if (_renderedDate != DateOnly.FromDateTime(now.LocalDateTime) && _week == ResetCalendar.Monday(_renderedDate))
            _week = ResetCalendar.Monday(DateOnly.FromDateTime(now.LocalDateTime));
        if (_nextBoundary <= now || _renderedDate != DateOnly.FromDateTime(now.LocalDateTime)) Render(); else UpdateTimes(now);
    }
    private void UpdateTimes(DateTimeOffset now)
    {
        foreach (var (entry, countdown, freshness) in _rows)
        {
            countdown.Text = entry.At is null ? "Non communiquée par Codex" : entry.At > now ? Display.Countdown(entry.At) : entry.Kind == ResetKind.Reserve ? "Expiration passée" : "Reset à confirmer dans Codex";
            countdown.SetResourceReference(TextBlock.ForegroundProperty, entry.At <= now || (entry.Kind == ResetKind.Reserve && entry.At - now <= TimeSpan.FromDays(1)) ? "WarningBrush" : "MutedBrush");
            freshness.Text = entry.Account.Error is not null ? "Dernier essai en échec · relevé conservé" : entry.Account.Snapshot is { } snapshot ? $"{(entry.Account.IsActiveInCodex ? "Actif · " : "")}relevé {Display.Age(snapshot.FetchedAt)}" : "Aucun relevé";
            freshness.ToolTip = $"Dernier relevé : {Display.Exact(entry.Account.Snapshot?.FetchedAt)}\n{entry.Account.Error}";
        }
    }
}
