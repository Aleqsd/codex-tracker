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
    private readonly List<(ResetScheduleEntry Entry, TextBlock Countdown, TextBlock Freshness)> _rows = [];
    private TrackerState _state = new([], null);
    private DateTimeOffset? _nextBoundary;
    private bool _syncing;

    internal ResetsView(PreferencesStore preferences, Action<Guid?> calendar, Action refresh)
    {
        _preferences = preferences;
        var root = new Grid { Margin = new Thickness(0, 17, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text("Les échéances de vos comptes", 20); title.FontWeight = FontWeights.SemiBold; heading.Children.Add(title);
        var export = new Button { Content = "Google Agenda ↗", Style = (Style)FindResource("QuietButton"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Ajouter une échéance ou importer les dates dans un agenda" };
        Grid.SetColumn(export, 1); heading.Children.Add(export);
        var introduction = new StackPanel(); introduction.Children.Add(heading); _summary.Margin = new Thickness(0, 7, 0, 5); introduction.Children.Add(_summary); introduction.Children.Add(_reserves); root.Children.Add(introduction);

        var filters = new Grid { Margin = new Thickness(0, 18, 0, 12) }; filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) }); filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _accounts = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id", Tag = FindResource("AccountsIcon") };
        export.Click += (_, _) => calendar((_accounts.SelectedItem as ResetAccountChoice)?.Id);
        System.Windows.Automation.AutomationProperties.SetName(_accounts, "Filtrer les resets par compte");
        _accounts.SelectionChanged += (_, _) => { if (!_syncing) Render(); }; filters.Children.Add(_accounts);
        _refresh = new Button { Content = "Actualiser", Style = (Style)FindResource("QuietButton"), ToolTip = "Actualise le compte actif dans Codex ; les autres conservent leur dernier relevé" };
        _refresh.Click += (_, _) => refresh(); Grid.SetColumn(_refresh, 2); filters.Children.Add(_refresh); Grid.SetRow(filters, 1); root.Children.Add(filters);
        var scroll = new ScrollViewer { Content = _timeline, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll); Content = root;
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
    private void Render()
    {
        var accountId = (_accounts.SelectedItem as ResetAccountChoice)?.Id;
        var accounts = _state.Accounts.Where(a => accountId is null || a.Profile.Id == accountId).ToArray();
        var entries = ResetSchedule.Entries(_state, accountId);
        var now = DateTimeOffset.UtcNow;
        var future = entries.Where(e => e.At > now).ToArray();
        var reached = entries.Where(e => e.At <= now).OrderByDescending(e => e.At).ToArray();
        var unknown = entries.Where(e => e.At is null).ToArray();
        _summary.Text = accounts.Length == 0 ? "Les dates apparaîtront dès qu’un compte sera détecté dans Codex." : $"{future.Length} à venir" + (unknown.Length > 0 ? " · certaines dates ne sont pas communiquées" : "");
        var counts = accounts.Select(a => a.Snapshot?.AvailableResetCredits).ToArray();
        _reserves.Text = counts.Length == 0 ? "" : counts.All(n => n is null) ? "Réserves : non communiquées" : $"Réserves : {counts.Sum(n => (long)(n ?? 0))} resets au dernier relevé" + (counts.Any(n => n is null) ? $" · {counts.Count(n => n is null)} comptes sans compteur" : "");
        _reserves.ToolTip = "Les comptes inactifs ne sont pas actualisés en arrière-plan. Le compteur serveur fait foi ; les dates détaillées ne permettent pas de déduire le nombre de resets disponibles.";
        _rows.Clear(); _timeline.Children.Clear();
        AddGroup("À venir", future);
        AddGroup("Dates atteintes · à vérifier", reached);
        AddGroup("Dates non communiquées", unknown, showCount: false);
        if (entries.Count == 0) { var empty = Ui.Text("Aucune échéance à afficher pour le moment.", 13, "MutedBrush"); empty.Margin = new Thickness(0, 24, 0, 0); _timeline.Children.Add(empty); }
        _nextBoundary = future.FirstOrDefault()?.At;
        UpdateTimes(now);
    }
    private void AddGroup(string title, IReadOnlyList<ResetScheduleEntry> entries, bool showCount = true)
    {
        if (entries.Count == 0) return;
        var heading = Ui.Text(showCount ? $"{title}  ·  {entries.Count}" : title, 12, "MutedBrush"); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 13, 0, 7); _timeline.Children.Add(heading);
        foreach (var entry in entries)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            var identity = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
            var account = Ui.Text(AccountName(entry.Account), 12); account.FontWeight = FontWeights.SemiBold; identity.Children.Add(account);
            var kind = entry.Kind switch { ResetKind.Weekly => "Reset hebdomadaire", ResetKind.Short => "Reset de la fenêtre 5 heures", _ => "Expiration d’un reset en réserve" };
            if (entry.IsUndetailedReserve) kind = entry.Account.Snapshot?.ResetCredits is { Count: > 0 } ? "Autres réserves · dates non communiquées" : "Expiration des resets en réserve";
            var detail = Ui.Text(kind, 11, "MutedBrush"); detail.Margin = new Thickness(0, 4, 0, 0); identity.Children.Add(detail);
            var freshness = Ui.Text("", 11, "MutedBrush"); freshness.Margin = new Thickness(0, 4, 0, 0); identity.Children.Add(freshness);
            if (entry.GrantedAt is { } granted) { var receipt = Ui.Text($"Reçu le {Display.Exact(granted)}", 11, "MutedBrush"); receipt.Margin = new Thickness(0, 4, 0, 0); receipt.ToolTip = Display.Zone(granted); identity.Children.Add(receipt); }
            row.Children.Add(identity);
            var timing = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var exact = Ui.Text(entry.At is null ? "Date indisponible" : Display.Exact(entry.At), 12); exact.FontWeight = FontWeights.SemiBold; timing.Children.Add(exact);
            var countdown = Ui.Text("", 11, "MutedBrush"); countdown.Margin = new Thickness(0, 4, 0, 0); timing.Children.Add(countdown);
            if (entry.At is { } at) { var zone = Ui.Text($"UTC{at.ToLocalTime():zzz}", 11, "MutedBrush"); zone.Margin = new Thickness(0, 4, 0, 0); zone.ToolTip = Display.Zone(at); timing.Children.Add(zone); }
            Grid.SetColumn(timing, 1); row.Children.Add(timing);
            var border = new Border { Child = row, Background = Brushes.Transparent, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12, 7, 13) }; border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            border.ToolTip = entry.Account.Snapshot is { } snapshot ? $"Dernier relevé : {Display.Exact(snapshot.FetchedAt)}\n{Display.Zone(snapshot.FetchedAt)}\n{Display.ReserveSummary(snapshot)}\n{entry.CreditTitle}\n{entry.Account.Error}" : "Aucun relevé disponible pour ce compte.";
            _timeline.Children.Add(border); _rows.Add((entry, countdown, freshness));
        }
    }
    internal void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        if (_nextBoundary <= now) Render(); else UpdateTimes(now);
    }
    private void UpdateTimes(DateTimeOffset now)
    {
        foreach (var (entry, countdown, freshness) in _rows)
        {
            countdown.Text = entry.At is null ? "Non communiquée par Codex" : entry.At > now ? Display.Countdown(entry.At) : entry.Kind == ResetKind.Reserve ? "Expiration passée" : "Reset à confirmer dans Codex";
            countdown.SetResourceReference(TextBlock.ForegroundProperty, entry.At <= now || (entry.Kind == ResetKind.Reserve && entry.At - now <= TimeSpan.FromDays(1)) ? "WarningBrush" : "MutedBrush");
            freshness.Text = entry.Account.Error is not null ? "Dernier essai en échec · relevé conservé" : entry.Account.Snapshot is { } snapshot ? $"{(entry.Account.IsActiveInCodex ? "Actif · " : "")}relevé {Display.Age(snapshot.FetchedAt)}" : "Aucun relevé";
        }
    }
}
