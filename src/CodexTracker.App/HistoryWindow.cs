using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;

namespace CodexTracker.App;

internal sealed record PeriodChoice(int Hours, string Label) { public override string ToString() => Label; }
internal sealed record WindowChoice(UsageWindowKind Value, string Label) { public override string ToString() => Label; }
internal sealed class HistoryWindow : ThemedWindow
{
    private readonly ITrackerService _service;
    private readonly PreferencesStore _preferences;
    private readonly ThemeManager _theme;
    private readonly TextBlock _name, _subtitle, _freshness, _period, _forecast, _forecastHint, _historyHint, _error;
    private readonly UsageChart _chart;
    private readonly ComboBox _periodSelector, _windowSelector;
    private readonly Button _remove;
    private readonly TextBlock _details;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private AccountViewModel? _model;
    private UsageForecast? _currentForecast;
    private bool _closed;
    public Guid AccountId { get; }

    public HistoryWindow(Window owner, ITrackerService service, PreferencesStore preferences, Guid accountId, ThemeManager theme)
        : base(owner, "Détails et historique", theme, 690, 745)
    {
        _service = service; _preferences = preferences; _theme = theme; AccountId = accountId;
        _name = Ui.Text("", 19); _name.FontWeight = FontWeights.SemiBold; Body.Children.Add(_name);
        _subtitle = Ui.Text("", 12, "MutedBrush"); _subtitle.Margin = new Thickness(0, 5, 0, 0); Body.Children.Add(_subtitle);
        _freshness = Ui.Text("", 11, "MutedBrush"); _freshness.Margin = new Thickness(0, 6, 0, 16); Body.Children.Add(_freshness);
        var metrics = new Grid(); for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
        AddMetric(metrics, 0, "Semaine", "WeeklyNumber"); AddMetric(metrics, 1, "5 heures", "ShortWindowRemaining"); AddMetric(metrics, 2, "Resets en réserve", "ReserveCount"); Body.Children.Add(Ui.Panel(metrics));
        _period = Ui.Text("", 11, "MutedBrush"); _period.Margin = new Thickness(0, 11, 0, 19); Body.Children.Add(_period);
        var chartHeader = new Grid { Margin = new Thickness(0, 0, 0, 11) }; chartHeader.ColumnDefinitions.Add(new ColumnDefinition()); chartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); chartHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chartTitle = Ui.Text("Quota restant", 13); chartTitle.FontWeight = FontWeights.SemiBold; chartTitle.VerticalAlignment = VerticalAlignment.Center; chartHeader.Children.Add(chartTitle);
        _windowSelector = new ComboBox { Width = 112, Margin = new Thickness(0, 0, 8, 0), ItemsSource = new[] { new WindowChoice(UsageWindowKind.Weekly, "Semaine"), new WindowChoice(UsageWindowKind.Short, "5 heures") }, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedIndex = 0 };
        _periodSelector = new ComboBox { Width = 108, ItemsSource = new[] { new PeriodChoice(24, "24 heures"), new PeriodChoice(168, "7 jours") }, DisplayMemberPath = "Label", SelectedValuePath = "Hours", SelectedIndex = 0 };
        Grid.SetColumn(_windowSelector, 1); Grid.SetColumn(_periodSelector, 2); chartHeader.Children.Add(_windowSelector); chartHeader.Children.Add(_periodSelector); Body.Children.Add(chartHeader);
        _chart = new UsageChart { Height = 167 }; Body.Children.Add(Ui.Panel(_chart, new Thickness(4, 7, 9, 0)));
        _historyHint = Ui.Text("", 10, "MutedBrush"); _historyHint.Margin = new Thickness(0, 8, 0, 16); Body.Children.Add(_historyHint);
        var forecast = new StackPanel(); var forecastLabel = Ui.Text("Estimation prudente", 11, "MutedBrush"); forecast.Children.Add(forecastLabel);
        _forecast = Ui.Text("", 14); _forecast.FontWeight = FontWeights.SemiBold; _forecast.Margin = new Thickness(0, 6, 0, 0); forecast.Children.Add(_forecast);
        _forecastHint = Ui.Text("", 11, "MutedBrush"); _forecastHint.Margin = new Thickness(0, 6, 0, 0); forecast.Children.Add(_forecastHint); Body.Children.Add(Ui.Panel(forecast));
        _error = Ui.Text("", 11, "DangerBrush"); _error.Margin = new Thickness(0, 10, 0, 0); Body.Children.Add(_error);
        _details = Ui.Text("", 11, "MutedBrush"); _details.LineHeight = 19; _details.Margin = new Thickness(0, 12, 0, 3);
        var personal = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var customize = new Button { Content = "Nom et avatar…", Style = (Style)FindResource("QuietButton"), Margin = new Thickness(-10, 0, 10, 0) };
        customize.Click += (_, _) => new AccountAppearanceWindow(this, preferences, AccountId, theme, _model?.IdentityHint).ShowDialog(); personal.Children.Add(customize);
        var calendar = new Button { Content = "Exporter les échéances…", Style = (Style)FindResource("QuietButton") };
        calendar.Click += (_, _) => new CalendarWindow(this, service, preferences, theme, AccountId).ShowDialog(); personal.Children.Add(calendar); Body.Children.Add(personal);
        var expander = new Expander { Header = "Dates exactes et détails", Content = _details, Margin = new Thickness(0, 15, 0, 0) }; expander.SetResourceReference(ForegroundProperty, "TextBrush"); Body.Children.Add(expander);
        var actions = new Grid { Margin = new Thickness(0, 17, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _remove = new Button { Content = "Retirer du suivi", Style = (Style)FindResource("QuietButton") }; _remove.Click += async (_, _) => await RemoveAsync(); Grid.SetColumn(_remove, 1); actions.Children.Add(_remove); Body.Children.Add(actions);
        _periodSelector.SelectionChanged += (_, _) => DrawChart(); _windowSelector.SelectionChanged += (_, _) => DrawChart();
        service.Changed += Changed; preferences.Changed += Changed; theme.Changed += Changed;
        _clock.Tick += (_, _) => UpdateClock();
        Closed += (_, _) => { _closed = true; _clock.Stop(); service.Changed -= Changed; preferences.Changed -= Changed; theme.Changed -= Changed; };
        Update(); _clock.Start();
    }
    private static void AddMetric(Grid grid, int index, string title, string path)
    {
        var stack = new StackPanel(); stack.Children.Add(Ui.Text(title, 10, "MutedBrush"));
        var value = Ui.Text("", 23); value.FontWeight = FontWeights.SemiBold; value.Margin = new Thickness(0, 5, 0, 0); value.SetBinding(TextBlock.TextProperty, new Binding(path)); stack.Children.Add(value);
        Grid.SetColumn(stack, index); grid.Children.Add(stack);
    }
    private void Changed(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Update);
    private void Update()
    {
        if (_closed) return;
        var account = _service.State.Accounts.FirstOrDefault(a => a.Profile.Id == AccountId);
        if (account is null) { Close(); return; }
        var vm = new AccountViewModel(account, _service.State, _preferences); _model = vm; DataContext = vm;
        _name.Text = vm.Email; _subtitle.Text = vm.PlanBadge; _freshness.Text = vm.Freshness;
        _period.Text = $"Période d’abonnement : {vm.SubscriptionSummary}"; _period.ToolTip = vm.SubscriptionDetails;
        _details.Text = vm.AllDetails; _remove.IsEnabled = vm.CanRemove;
        _error.Text = vm.Error; _error.Visibility = vm.HasError ? Visibility.Visible : Visibility.Collapsed;
        var forecast = _service.GetForecast(AccountId); _currentForecast = forecast;
        _forecast.ToolTip = forecast.EstimatedExhaustionAt is DateTimeOffset time ? $"Épuisement estimé : {Display.Exact(time)}\n{Display.Zone(time)}" : null;
        _forecastHint.Text = Display.SafeText(forecast.Explanation, _preferences.Current.PrivacyMode); UpdateClock(); DrawChart();
    }
    private void UpdateClock()
    {
        if (_closed || _model is null) return;
        _model.Tick(); _freshness.Text = _model.Freshness; _details.Text = _model.AllDetails;
        _forecast.Text = _currentForecast?.EstimatedExhaustionAt is DateTimeOffset at
            ? at <= DateTimeOffset.UtcNow ? "Échéance estimée atteinte · à réévaluer"
                : $"{(_currentForecast.Window == UsageWindowKind.Short ? "Fenêtre 5 h" : "Quota hebdomadaire")} · {Display.Countdown(at).ToLowerInvariant()}"
            : "Pas d’estimation fiable pour le moment";
    }
    private void DrawChart()
    {
        if (_closed || _chart is null || _periodSelector.SelectedValue is not int hours || _windowSelector.SelectedValue is not UsageWindowKind window) return;
        var samples = _service.GetHistory(AccountId); var cutoff = DateTimeOffset.UtcNow.AddHours(-hours);
        _chart.Samples = samples; _chart.Hours = hours; _chart.Window = window; _chart.InvalidateVisual();
        int count = samples.Count(s => s.Timestamp >= cutoff && (window == UsageWindowKind.Weekly ? s.WeeklyRemaining : s.ShortRemaining) is not null);
        _historyHint.Text = count == 0 ? "Aucun relevé sur cette période. L’historique se construit quand le compte est actif." : $"{count} relevés · les interruptions et les resets restent visibles dans la courbe.";
    }
    private async Task RemoveAsync()
    {
        var account = _service.State.Accounts.FirstOrDefault(a => a.Profile.Id == AccountId); if (account is null) return;
        string name = PrivacyText.Account(account.Profile, _service.State, _preferences.Current);
        if (new TrackerDialog(this, "Retirer ce compte du suivi ?", $"{name}\n\nSon historique local sera supprimé. Il réapparaîtra quand vous l’ouvrirez dans Codex.", "Retirer", "Annuler").ShowDialog() != true) return;
        try
        {
            await _service.RemoveAccountAsync(AccountId);
            var avatar = _preferences.Current.Appearances.GetValueOrDefault(AccountId)?.AvatarFile;
            _preferences.Update(p =>
            {
                var appearances = new Dictionary<Guid, AccountAppearance>(p.Appearances); appearances.Remove(AccountId);
                return p with { Appearances = appearances };
            });
            AvatarStore.Remove(_preferences.DataDirectory, avatar); Close();
        }
        catch (Exception error) { if (!_closed) ShowError(error.Message); }
    }
    private void ShowError(string message) => new TrackerDialog(this, "Action indisponible", Display.SafeText(message, _preferences.Current.PrivacyMode), "Fermer", null).ShowDialog();
}

internal sealed class UsageChart : FrameworkElement
{
    public IReadOnlyList<UsageSample> Samples { get; set; } = [];
    public int Hours { get; set; } = 24;
    public UsageWindowKind Window { get; set; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (ActualWidth < 60 || ActualHeight < 60) return;
        double left = 36, top = 12, width = ActualWidth - 51, height = ActualHeight - 42;
        var now = DateTimeOffset.UtcNow; var start = now.AddHours(-Hours);
        var grid = new Pen(ThemeManager.GetBrush("LineBrush"), 0.7); var stroke = new Pen(ThemeManager.GetBrush("ChartBrush"), 1.8);
        foreach (int tick in new[] { 0, 50, 100 })
        {
            double y = top + height * (1 - tick / 100.0); dc.DrawLine(grid, new Point(left, y), new Point(left + width, y)); DrawText(dc, tick == 100 ? "100%" : tick.ToString(), 3, y - 6);
        }
        DrawText(dc, Hours > 24 ? start.ToLocalTime().ToString("dd/MM") : start.ToLocalTime().ToString("HH:mm"), left, top + height + 9);
        DrawText(dc, "Maintenant", left + width - 53, top + height + 9);
        UsageSample? previous = null; Point? lastPoint = null; int known = 0;
        foreach (var sample in Samples.Where(s => s.Timestamp >= start && s.Timestamp <= now).OrderBy(s => s.Timestamp))
        {
            double? remaining = Window == UsageWindowKind.Weekly ? sample.WeeklyRemaining : sample.ShortRemaining;
            if (remaining is null || !double.IsFinite(remaining.Value)) { previous = null; lastPoint = null; continue; }
            var point = new Point(left + (sample.Timestamp - start).TotalSeconds / (now - start).TotalSeconds * width, top + height * (1 - Math.Clamp(remaining.Value, 0, 100) / 100));
            bool join = previous is not null && lastPoint is not null && sample.Timestamp - previous.Timestamp <= TimeSpan.FromMinutes(5)
                && (Window == UsageWindowKind.Weekly ? sample.WeeklyResetsAt == previous.WeeklyResetsAt && remaining <= previous.WeeklyRemaining : sample.ShortResetsAt == previous.ShortResetsAt && remaining <= previous.ShortRemaining);
            if (join) dc.DrawLine(stroke, lastPoint!.Value, point);
            else dc.DrawEllipse(ThemeManager.GetBrush("ChartBrush"), null, point, 1.7, 1.7);
            lastPoint = point; previous = sample; known++;
        }
        if (known == 0) DrawText(dc, "Les premiers relevés apparaîtront ici.", Math.Max(left, left + width / 2 - 93), top + height / 2 - 6);
        else if (lastPoint is Point last) dc.DrawEllipse(ThemeManager.GetBrush("ChartBrush"), null, last, 2.7, 2.7);
    }
    private void DrawText(DrawingContext dc, string text, double x, double y)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, ThemeManager.GetBrush("MutedBrush"), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }
}
