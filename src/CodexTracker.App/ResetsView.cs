namespace CodexTracker.App;

internal sealed record ResetAccountChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}
internal sealed record ResetTypeChoice(ResetKind? Kind, string Label)
{
    public override string ToString() => Label;
}

internal sealed class ResetsView : UserControl
{
    private readonly PreferencesStore _preferences;
    private readonly ComboBox _accounts, _kinds;
    private readonly StackPanel _timeline = new();
    private readonly TextBlock _summary = Ui.Text("", 12, "MutedBrush");
    private readonly Button _refresh;
    private readonly List<(ResetScheduleEntry Entry, TextBlock Countdown, TextBlock Freshness)> _rows = [];
    private readonly HashSet<string> _openEntries = [];
    private readonly HashSet<string> _openAnnouncements = [];
    private readonly List<GlobalResetCard> _announcementCards = [];
    private TrackerState _state = new([], null);
    private DateTimeOffset? _nextBoundary;
    private bool _syncing, _weekView, _showReached, _showUnknown, _showReserves, _showAnnouncements;
    private ResetKind? _kindFilter;
    private DateOnly _week = ResetCalendar.Monday(DateOnly.FromDateTime(PreviewClock.UtcNow.LocalDateTime));
    private DateOnly? _day;
    private DateOnly _renderedDate;
    private readonly RadioButton _agendaChoice, _weekChoice;
    private readonly SelectionPill? _viewPill;

    internal ResetsView(PreferencesStore preferences, Action<Guid?> calendar, Action refresh, Action? declareReset = null)
    {
        _preferences = preferences;
        _accounts = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id", Tag = FindResource("AccountsIcon"), Margin = new Thickness(0, 0, 8, 0) };
        var root = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var introduction = new StackPanel();
        var title = Ui.Text(Loc.T("Prochains resets"), 22); title.FontWeight = FontWeights.Medium; introduction.Children.Add(title);
        _summary.Margin = new Thickness(0, 5, 0, 0); introduction.Children.Add(_summary); heading.Children.Add(introduction);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (declareReset is not null)
        {
            var declare = new Button { Content = Loc.T("Reset Codex…"), Style = (Style)FindResource("QuietButton"), Padding = new Thickness(11, 7, 11, 7), Margin = new Thickness(0, 0, 4, 0), ToolTip = Loc.T("Déclarer ou modifier l’heure d’un reset général Codex") };
            declare.Click += (_, _) => declareReset(); actions.Children.Add(declare);
        }
        _refresh = Ui.IconButton("DropdownRefreshIcon", Loc.T("Actualiser les comptes actifs de Codex et Claude Code"), Loc.T("Actualiser les comptes actifs"));
        _refresh.Click += (_, _) => { UiMotion.Spin((FrameworkElement)_refresh.Content); refresh(); }; actions.Children.Add(_refresh);
        var more = Ui.IconButton("MoreIcon", Loc.T("Autres actions"), Loc.T("Autres actions des resets"), 18, 3);
        more.Margin = new Thickness(2, 0, 0, 0);
        var export = new MenuItem { Header = Loc.T("Google Agenda ↗") };
        export.Click += (_, _) => calendar((_accounts.SelectedItem as ResetAccountChoice)?.Id);
        more.ContextMenu = new ContextMenu { Items = { export } };
        more.Click += (_, _) => { more.ContextMenu.PlacementTarget = more; more.ContextMenu.IsOpen = true; };
        actions.Children.Add(more); Grid.SetColumn(actions, 1); heading.Children.Add(actions); root.Children.Add(heading);

        var filters = new Grid { Margin = new Thickness(0, 18, 0, 6) };
        filters.ColumnDefinitions.Add(new ColumnDefinition());
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        System.Windows.Automation.AutomationProperties.SetName(_accounts, Loc.T("Filtrer les resets par compte"));
        _accounts.SelectionChanged += (_, _) => { if (!_syncing) Render(animate: true); }; filters.Children.Add(_accounts);
        _kinds = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Kind", Tag = FindResource("DropdownClockIcon"), Margin = new Thickness(0, 0, 10, 0),
            ItemsSource = new ResetTypeChoice[] { new(null, Loc.T("Tous les types")), new(ResetKind.Weekly, Loc.T("Hebdomadaires")), new(ResetKind.Short, Loc.T("5 heures")), new(ResetKind.Reserve, Loc.T("Réserves")) }, SelectedIndex = 0 };
        System.Windows.Automation.AutomationProperties.SetName(_kinds, Loc.T("Filtrer les resets par type"));
        _kinds.SelectionChanged += (_, _) => { _kindFilter = (_kinds.SelectedItem as ResetTypeChoice)?.Kind; Render(animate: true); };
        Grid.SetColumn(_kinds, 1); filters.Children.Add(_kinds);
        var views = new StackPanel { Orientation = Orientation.Horizontal };
        _agendaChoice = new RadioButton { Content = Loc.T("Liste"), GroupName = "ResetView", IsChecked = true, Style = (Style)FindResource("ResetKindFilter") };
        _weekChoice = new RadioButton { Content = Loc.T("Semaine"), GroupName = "ResetView", Style = (Style)FindResource("ResetKindFilter") };
        _agendaChoice.Checked += (_, _) => { _weekView = false; _viewPill?.Move(); Render(animate: true); };
        _weekChoice.Checked += (_, _) => { _weekView = true; _viewPill?.Move(); Render(animate: true); };
        System.Windows.Automation.AutomationProperties.SetName(_agendaChoice, Loc.T("Vue liste des resets"));
        System.Windows.Automation.AutomationProperties.SetName(_weekChoice, Loc.T("Vue semaine des resets"));
        views.Children.Add(_agendaChoice); views.Children.Add(_weekChoice);
        var pill = new Border { CornerRadius = new CornerRadius(7) }; pill.SetResourceReference(Border.BackgroundProperty, "SegmentBrush");
        var segment = new Grid(); segment.Children.Add(pill); segment.Children.Add(views);
        _viewPill = new SelectionPill(segment, pill, () => _weekChoice.IsChecked == true ? _weekChoice : _agendaChoice);
        var track = new Border { Child = segment, Style = (Style)FindResource("SegmentTrack"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(track, 2); filters.Children.Add(track);
        Grid.SetRow(filters, 1); root.Children.Add(filters);
        _timeline.Margin = new Thickness(0, 0, 6, 20);
        UiMotion.SetRise(_timeline, 10); UiMotion.SetStagger(_timeline, true);
        var scroll = new ScrollViewer { Content = _timeline, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll); Content = root;
    }

    internal void Update(TrackerState state)
    {
        _state = state with { ManualCodexReset = _preferences.Current.ManualCodexReset }; _refresh.IsEnabled = !state.IsBusy;
        var selected = (_accounts.SelectedItem as ResetAccountChoice)?.Id;
        var choices = new[] { new ResetAccountChoice(null, Loc.T("Tous les comptes")) }
            .Concat(state.Accounts.Select(a => new ResetAccountChoice(a.Profile.Id, AccountName(a)))).ToArray();
        if (_accounts.ItemsSource is not ResetAccountChoice[] previous || !previous.SequenceEqual(choices))
        {
            _syncing = true; _accounts.ItemsSource = choices;
            _accounts.SelectedItem = choices.FirstOrDefault(c => c.Id == selected) ?? choices[0]; _syncing = false;
        }
        var current = ResetSchedule.Entries(_state, now: PreviewClock.UtcNow).Select(EntryKey).ToHashSet();
        _openEntries.IntersectWith(current);
        Render();
    }
    private string AccountLabel(AccountState account) => PrivacyText.Account(account.Profile, _state, _preferences.Current) +
        (!_preferences.Current.PrivacyMode && account.Profile.OrganizationName is { } organization ? " · " + organization : "");
    private string AccountName(AccountState account) => account.Profile.ProviderName + " · " + AccountLabel(account);
    private static string KindLabel(ResetScheduleEntry entry) => entry.IsUndetailedReserve ? Loc.T("Réserves sans date") : entry.Kind switch
    { ResetKind.Weekly => Loc.T("Reset hebdomadaire"), ResetKind.Short => Loc.T("Reset 5 heures"), _ => Loc.T("Expiration de réserve") };
    private static string EntryKey(ResetScheduleEntry entry) => $"{entry.Account.Profile.Id}:{entry.Kind}:{entry.CreditId}:{entry.At:O}";
    internal void ShowWeek() => _weekChoice.IsChecked = true;
    internal void ShowAnnouncements()
    {
        _showAnnouncements = true; _accounts.SelectedIndex = 0; _kinds.SelectedIndex = 0; _agendaChoice.IsChecked = true;
        Render(animate: true);
    }

    private void Render(bool animate = false)
    {
        var accountId = (_accounts.SelectedItem as ResetAccountChoice)?.Id;
        var accounts = _state.Accounts.Where(a => accountId is null || a.Profile.Id == accountId).ToArray();
        var now = PreviewClock.UtcNow;
        var entries = ResetSchedule.Entries(_state, accountId, now).Where(e => _kindFilter is null || e.Kind == _kindFilter).ToArray();
        // Reserve expirations are secondary to quota resets unless explicitly selected.
        var schedule = entries.Where(e => _kindFilter == ResetKind.Reserve || e.Kind != ResetKind.Reserve).ToArray();
        var future = schedule.Where(e => e.At > now).ToArray();
        var reached = schedule.Where(e => e.At <= now).OrderByDescending(e => e.At).ToArray();
        var unknown = schedule.Where(e => e.At is null).ToArray();
        _renderedDate = DateOnly.FromDateTime(now.LocalDateTime);
        _summary.Text = accounts.Length == 0 ? Loc.T("Détectez un compte pour retrouver ses prochaines échéances.")
            : future.Length == 1 ? Loc.T("1 échéance à venir") : Loc.F("{0} échéances à venir", future.Length);
        _summary.ToolTip = Loc.T("Selon les derniers relevés. Les comptes inactifs ne sont pas actualisés en arrière-plan.");
        _rows.Clear(); _timeline.Children.Clear(); _announcementCards.Clear();
        if (_state.ManualCodexReset is { } manual && accounts.Any(a => manual.Applies(a, ResetKind.Weekly, now) || manual.Applies(a, ResetKind.Short, now)))
        {
            var notice = Ui.Text(Loc.F("Reset Codex déclaré · {0:dd/MM à HH:mm}", manual.At.ToLocalTime()), 12, "AccentBrush");
            var chip = new Border { Child = notice, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 10, 0, 2), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = $"{Display.Exact(manual.At)} · {Display.Zone(manual.At)}\n" + Loc.T("Les prochaines échéances seront confirmées par de nouveaux relevés.") };
            chip.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            _timeline.Children.Add(chip);
        }
        AddGlobalAnnouncements(accounts, now);
        if (_weekView) AddWeek(schedule, now);
        else
        {
            AddDays(_timeline, future, now);
            if (future.Length == 0) _timeline.Children.Add(Empty(accounts.Length == 0 ? Loc.T("Aucun compte détecté.") : Loc.T("Aucune date à venir connue pour cette sélection.")));
            AddSecondary("ReachedResets", Loc.F("À confirmer · {0}", reached.Length), reached, _showReached, open => _showReached = open);
        }
        AddSecondary("UnknownResets", Loc.F("Dates inconnues · {0}", unknown.Length), unknown, _showUnknown, open => _showUnknown = open);
        if (_kindFilter is null) AddReserves(accounts, entries.Where(e => e.Kind == ResetKind.Reserve).ToArray(), now);
        _nextBoundary = entries.Select(e => e.At)
            .Concat((_state.GlobalResetFeed?.Announcements ?? []).SelectMany(a => new DateTimeOffset?[] { a.ReportedAt.AddHours(5), a.ReportedAt.AddHours(24) }))
            .Where(at => at > now).Order().FirstOrDefault();
        UpdateTimes(now);
        if (animate) UiMotion.FadeIn(_timeline);
    }
    private static TextBlock Empty(string text)
    {
        var block = Ui.Text(text, 13, "MutedBrush"); block.Margin = new Thickness(2, 16, 0, 18); return block;
    }
    /// <summary>A card holding schedule rows; rows add their own separators.</summary>
    private static StackPanel Group(Panel target)
    {
        var rows = new StackPanel(); target.Children.Add(Ui.Panel(rows, new Thickness(4))); return rows;
    }
    private Expander Disclosure(string name, string title, StackPanel content, bool open, Action<bool> changed)
    {
        var header = Ui.Text(title, 12); header.FontWeight = FontWeights.Medium;
        var section = new Expander { Name = name, Header = header, Content = content, IsExpanded = open, Margin = new Thickness(0, 18, 0, 2), FontSize = 12 };
        section.Expanded += (_, e) => { if (ReferenceEquals(e.OriginalSource, section)) changed(true); };
        section.Collapsed += (_, e) => { if (ReferenceEquals(e.OriginalSource, section)) changed(false); };
        return section;
    }
    private void AddSecondary(string name, string title, ResetScheduleEntry[] entries, bool open, Action<bool> changed)
    {
        if (entries.Length == 0) return;
        var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var rows = Group(content);
        foreach (var entry in entries) AddEntry(rows, entry);
        _timeline.Children.Add(Disclosure(name, title, content, open, changed));
    }
    private void AddGlobalAnnouncements(AccountState[] accounts, DateTimeOffset now)
    {
        accounts = accounts.Where(a => a.Profile.Provider == AccountProvider.Codex).ToArray();
        if (accounts.Length == 0 || _kindFilter == ResetKind.Reserve) return;
        var content = new StackPanel();
        foreach (var announcement in (_state.GlobalResetFeed?.Announcements ?? []).Where(a => a.IsCurrent(now)).OrderByDescending(a => a.ReportedAt))
        {
            var kinds = announcement.Kinds.Where(k => _kindFilter is null || k == _kindFilter).Distinct().Order().ToArray();
            if (kinds.Length == 0) continue;
            var card = new GlobalResetCard(announcement, accounts, kinds, AccountName, _state.GlobalResetFeed?.Error,
                _openAnnouncements.Contains(announcement.Id), open =>
                { if (open) _openAnnouncements.Add(announcement.Id); else _openAnnouncements.Remove(announcement.Id); }, _state.ManualCodexReset);
            _announcementCards.Add(card); content.Children.Add(card);
        }
        if (content.Children.Count > 0)
            _timeline.Children.Add(Disclosure("ResetAnnouncements", Loc.T("Reset général annoncé · voir les sources"), content, _showAnnouncements, open => _showAnnouncements = open));
    }
    private void AddReserves(AccountState[] accounts, ResetScheduleEntry[] entries, DateTimeOffset now)
    {
        var codex = accounts.Where(a => a.Profile.Provider == AccountProvider.Codex).ToArray();
        if (codex.Length == 0) return;
        var counts = codex.Select(a => a.Snapshot?.AvailableResetCredits).ToArray();
        var title = counts.All(n => n is null) ? Loc.T("Réserves Codex · non communiquées")
            : Loc.F("Réserves Codex · {0} resets au dernier relevé", counts.Sum(n => (long)(n ?? 0))) + (counts.Any(n => n is null) ? " · " + Loc.T("compteurs incomplets") : "");
        var content = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var priority = ResetCalendar.PriorityReserve(_state, now, (_accounts.SelectedItem as ResetAccountChoice)?.Id);
        if (priority is not null)
        {
            var note = Ui.Text(Loc.F("À utiliser en priorité : {0} · {1}", AccountLabel(priority.Account), Display.Countdown(priority.At)), 12, "MutedBrush");
            note.Margin = new Thickness(2, 2, 0, 10); note.ToolTip = EntryHint(priority); content.Children.Add(note);
        }
        if (entries.Length > 0) { var rows = Group(content); foreach (var entry in entries) AddEntry(rows, entry); }
        else content.Children.Add(Empty(Loc.T("Aucune réserve à afficher pour cette sélection.")));
        var section = Disclosure("ReserveResets", title, content, _showReserves, open => _showReserves = open);
        section.ToolTip = Loc.T("Compteurs serveur au dernier relevé. Les dates d’expiration ne permettent pas de déduire le nombre de resets disponibles.");
        _timeline.Children.Add(section);
    }
    private string DateLabel(DateOnly date, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        return date == today ? Loc.T("Aujourd’hui") : date == today.AddDays(1) ? Loc.T("Demain") : date.ToString(date.Year == today.Year ? "dddd dd MMMM" : "dddd dd MMMM yyyy");
    }
    private void AddDays(StackPanel target, IEnumerable<ResetScheduleEntry> entries, DateTimeOffset now)
    {
        foreach (var day in entries.GroupBy(e => DateOnly.FromDateTime(e.At!.Value.LocalDateTime)))
        {
            var label = Ui.Text(DateLabel(day.Key, now), 12, "MutedBrush"); label.FontWeight = FontWeights.Medium; label.Margin = new Thickness(2, 16, 0, 8); target.Children.Add(label);
            var rows = Group(target);
            foreach (var entry in day) AddEntry(rows, entry);
        }
    }
    private void AddWeek(ResetScheduleEntry[] entries, DateTimeOffset now)
    {
        var navigation = new Grid { Margin = new Thickness(2, 10, 0, 10) };
        navigation.ColumnDefinitions.Add(new ColumnDefinition()); navigation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var range = Ui.Text($"{_week:dd MMM} – {_week.AddDays(6):dd MMM yyyy}", 13); range.FontWeight = FontWeights.Medium; range.VerticalAlignment = VerticalAlignment.Center; navigation.Children.Add(range);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (icon, name, shift) in new[] { ("ChevronLeftIcon", Loc.T("Semaine précédente"), -7), ("", Loc.T("Semaine actuelle"), 0), ("ChevronRightIcon", Loc.T("Semaine suivante"), 7) })
        {
            Button button;
            if (icon.Length == 0)
            {
                button = new Button { Content = Loc.T("Cette semaine"), ToolTip = name, Style = (Style)FindResource("QuietButton"), Padding = new Thickness(10, 6, 10, 6) };
                System.Windows.Automation.AutomationProperties.SetName(button, name);
            }
            else { button = Ui.IconButton(icon, name, name); button.Width = button.Height = 30; }
            button.Click += (_, _) => { _week = shift == 0 ? ResetCalendar.Monday(DateOnly.FromDateTime(PreviewClock.UtcNow.LocalDateTime)) : _week.AddDays(shift); _day = null; Render(animate: true); };
            buttons.Children.Add(button);
        }
        Grid.SetColumn(buttons, 1); navigation.Children.Add(buttons); _timeline.Children.Add(navigation);
        var days = ResetCalendar.Week(entries, _week, TimeZoneInfo.Local);
        var strip = new System.Windows.Controls.Primitives.UniformGrid { Columns = 7 };
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        foreach (var day in days)
        {
            var dayBrush = day.Date == today ? "AccentBrush" : "TextBrush";
            var content = new StackPanel();
            var name = Ui.Text(day.Date.ToString("ddd"), 11, day.Date == today ? "AccentBrush" : "MutedBrush"); name.TextAlignment = TextAlignment.Center; content.Children.Add(name);
            var date = Ui.Text(day.Date.ToString("dd"), 19, dayBrush); date.FontWeight = day.Date == today ? FontWeights.Bold : FontWeights.Medium; date.TextAlignment = TextAlignment.Center; date.Margin = new Thickness(0, 3, 0, 4); content.Children.Add(date);
            var count = Ui.Text(day.Entries.Count == 0 ? "—" : day.Entries.Count == 1 ? Loc.T("1 reset") : Loc.F("{0} resets", day.Entries.Count), 10, day.Entries.Count == 0 ? "SubtleBrush" : "MutedBrush"); count.TextAlignment = TextAlignment.Center; content.Children.Add(count);
            var select = new Button { Content = content, Style = (Style)FindResource("QuietButton"), Padding = new Thickness(2, 9, 2, 9), Margin = new Thickness(2, 0, 2, 0), FontWeight = FontWeights.Normal };
            if (_day == day.Date) { select.SetResourceReference(BackgroundProperty, "AccentSoftBrush"); select.SetResourceReference(BorderBrushProperty, "AccentBrush"); }
            select.Click += (_, _) => { _day = _day == day.Date ? null : day.Date; Render(animate: true); };
            System.Windows.Automation.AutomationProperties.SetName(select, Loc.F("Échéances du {0:dd/MM/yyyy}, {1} resets", day.Date, day.Entries.Count));
            strip.Children.Add(select);
        }
        _timeline.Children.Add(Ui.Panel(strip, new Thickness(5)));
        var visible = days.Where(d => _day is null || d.Date == _day).SelectMany(d => d.Entries).ToArray();
        _summary.Text = _day is null
            ? visible.Length == 1 ? Loc.T("1 échéance cette semaine") : Loc.F("{0} échéances cette semaine", visible.Length)
            : visible.Length == 1 ? Loc.T("1 échéance ce jour") : Loc.F("{0} échéances ce jour", visible.Length);
        AddDays(_timeline, visible, now);
        if (visible.Length == 0) _timeline.Children.Add(Empty(_day is null ? Loc.T("Aucune échéance connue cette semaine.") : Loc.T("Aucune échéance connue ce jour.")));
    }
    private string EntryHint(ResetScheduleEntry entry) =>
        $"{KindLabel(entry)}\n{AccountName(entry.Account)}\n{entry.CreditTitle}\n{Display.Exact(entry.At)}\n{(entry.At is { } at ? Display.Zone(at) : Loc.T("Date non communiquée"))}\n" +
        Loc.F("Relevé : {0}", Display.Exact(entry.Account.Snapshot?.FetchedAt)) + "\n" + entry.Account.Error;
    private void AddEntry(StackPanel target, ResetScheduleEntry entry)
    {
        var header = new Grid { Margin = new Thickness(0, 10, 6, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = entry.Account.Profile.ProviderName };
        icon.SetResourceReference(Image.SourceProperty, entry.Account.Profile.Provider == AccountProvider.ClaudeCode ? "ClaudeProviderIcon" : "CodexProviderIcon"); header.Children.Add(icon);
        var identity = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var account = Ui.Text(AccountLabel(entry.Account), 13); account.FontWeight = FontWeights.Medium; account.TextWrapping = TextWrapping.NoWrap; account.TextTrimming = TextTrimming.CharacterEllipsis; account.ToolTip = AccountName(entry.Account); identity.Children.Add(account);
        var kind = Ui.Text(KindLabel(entry), 11, "MutedBrush"); kind.Margin = new Thickness(0, 3, 0, 0); identity.Children.Add(kind); Grid.SetColumn(identity, 1); header.Children.Add(identity);
        var timing = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var countdown = Ui.Text("", 13); countdown.FontWeight = FontWeights.Medium; countdown.TextAlignment = TextAlignment.Right; timing.Children.Add(countdown);
        var exact = Ui.Text(entry.At?.ToLocalTime().ToString("HH:mm") ?? Loc.T("Date indisponible"), 11, "MutedBrush"); exact.TextAlignment = TextAlignment.Right; exact.Margin = new Thickness(0, 3, 0, 0); timing.Children.Add(exact); Grid.SetColumn(timing, 2); header.Children.Add(timing);
        var details = new StackPanel { Margin = new Thickness(48, 0, 20, 12) };
        if (!string.IsNullOrWhiteSpace(entry.CreditTitle)) details.Children.Add(Ui.Text(entry.CreditTitle, 12));
        details.Children.Add(Ui.Text(entry.At is { } at ? $"{Display.Exact(at)} · {Display.Zone(at)}" : Loc.F("Date non communiquée par {0}", entry.Account.Profile.ProviderName), 11, "MutedBrush"));
        if (entry.GrantedAt is { } granted) details.Children.Add(Ui.Text(Loc.F("Reçu le {0} · {1}", Display.Exact(granted), Display.Zone(granted)), 11, "MutedBrush"));
        var freshness = Ui.Text("", 11, "MutedBrush"); freshness.Margin = new Thickness(0, 7, 0, 0); details.Children.Add(freshness);
        var key = EntryKey(entry);
        var row = new Expander { Header = header, Content = details, Style = (Style)FindResource("ResetEntry"), Tag = entry, IsExpanded = _openEntries.Contains(key), ToolTip = EntryHint(entry) };
        if (target.Children.Count == 0) row.BorderThickness = new Thickness(0);
        row.Expanded += (_, _) => _openEntries.Add(key); row.Collapsed += (_, _) => _openEntries.Remove(key);
        System.Windows.Automation.AutomationProperties.SetName(row, Loc.F("{0} · {1} · détails", KindLabel(entry), AccountName(entry.Account)));
        target.Children.Add(row); _rows.Add((entry, countdown, freshness));
    }
    internal void Tick()
    {
        var now = PreviewClock.UtcNow;
        if (_renderedDate != DateOnly.FromDateTime(now.LocalDateTime) && _week == ResetCalendar.Monday(_renderedDate))
        { _week = ResetCalendar.Monday(DateOnly.FromDateTime(now.LocalDateTime)); _day = null; }
        if (_nextBoundary <= now || _renderedDate != DateOnly.FromDateTime(now.LocalDateTime)) Render(); else UpdateTimes(now);
    }
    private void UpdateTimes(DateTimeOffset now)
    {
        foreach (var card in _announcementCards) card.Tick();
        foreach (var (entry, countdown, freshness) in _rows)
        {
            var reset = ExpectedReset.For(entry.Account, entry.Kind, now, _state.GlobalResetFeed, _state.ManualCodexReset);
            var expected = reset is not null && (reset.Announcement is not null || reset.At == entry.At);
            countdown.Text = expected ? Loc.T("≈100 % · à confirmer") : entry.At is null ? Loc.T("Date inconnue") : entry.At > now ? Display.Countdown(entry.At) : entry.Kind == ResetKind.Reserve ? Loc.T("Expiration passée") : Loc.F("Reset à confirmer dans {0}", entry.Account.Profile.ProviderName);
            countdown.ToolTip = expected ? Loc.T("Quota probablement rechargé. Ouvrez le compte pour confirmer ; le dernier relevé reste conservé.") : null;
            countdown.SetResourceReference(TextBlock.ForegroundProperty, expected ? "GoodBrush" : entry.At <= now || (entry.Kind == ResetKind.Reserve && entry.At - now <= TimeSpan.FromDays(1)) ? "WarningBrush" : "TextBrush");
            freshness.Text = entry.Account.Error is not null ? Loc.T("Dernier essai en échec · relevé conservé")
                : entry.Account.Snapshot is { } snapshot ? entry.Account.IsActive ? Loc.F("Actif · relevé {0}", Display.Age(snapshot.FetchedAt)) : Loc.F("relevé {0}", Display.Age(snapshot.FetchedAt))
                : Loc.T("Aucun relevé");
            freshness.ToolTip = Loc.F("Dernier relevé : {0}", Display.Exact(entry.Account.Snapshot?.FetchedAt)) + "\n" + entry.Account.Error;
        }
    }
}
