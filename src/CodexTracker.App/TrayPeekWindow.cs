using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;

namespace CodexTracker.App;

// A non-activating window: a glance at the quota must not steal keyboard focus from Codex.
internal sealed class TrayPeekWindow : Window
{
    // Codex and Claude Code can both be in use: each active account gets its own section.
    private readonly StackPanel _sections = new();
    private readonly Action<AccountProvider, ResetKind>? _select;
    private DrawingPoint _anchor;
    private bool _positionQueued, _positioning, _closed, _offscreen;

    /// <param name="select">Called when a quota is clicked: it becomes the one drawn in the notification icon.</param>
    public TrayPeekWindow(Action open, Action<AccountProvider, ResetKind>? select = null)
    {
        _select = select;
        SetResourceReference(StyleProperty, typeof(Window));
        // The shared window style applies the same smoothing as the main interface.
        Title = Loc.T("Aperçu Codex Tracker");
        Width = 322;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var border = new Border { Padding = new Thickness(20, 18, 20, 20), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var content = new StackPanel(); border.Child = content;
        UiMotion.SetRise(content, 8); UiMotion.SetFadeOnShow(content, true);
        Content = new ScrollViewer { Content = border, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        content.Children.Add(_sections);
        var button = new Button { Content = Loc.T("Ouvrir le suivi"), Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(10, 8, 10, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => { Hide(); open(); }; content.Children.Add(button);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int rounded = 2; DwmSetWindowAttribute(hwnd, 33, ref rounded, sizeof(int));
            // Choosing the icon's quota with a click leaves the keyboard in Codex or the terminal.
            NoActivation(true);
        };
        // An explicit activation (keyboard, automation) lifts the style so that the window takes the keyboard.
        Activated += (_, _) => { NoActivation(false); SetFocus(new WindowInteropHelper(this).Handle); };
        Deactivated += (_, _) => NoActivation(true);
        DpiChanged += (_, _) => QueuePosition();
        SizeChanged += (_, _) => { if (!_positioning) QueuePosition(); };
        Closed += (_, _) => _closed = true;
    }

    private void NoActivation(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, ExtendedStyle);
        SetWindowLongPtr(hwnd, ExtendedStyle, enabled ? style | NoActivate : style & ~NoActivate);
    }

    public void Update(TrackerState state, TrackerPreferences preferences)
    {
        state = state with { ManualCodexReset = preferences.ManualCodexReset };
        IReadOnlyList<AccountState?> accounts = state.ActiveAccounts.Count > 0 ? [.. state.ActiveAccounts] : [null];
        // With one icon per tool, each section keeps its own choice.
        var icons = TrayQuota.Icons(state, preferences);
        while (_sections.Children.Count < accounts.Count) _sections.Children.Add(new AccountPeek(_select));
        for (int i = 0; i < _sections.Children.Count; i++)
        {
            var section = (AccountPeek)_sections.Children[i];
            section.Visibility = i < accounts.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i < accounts.Count) section.Update(state, accounts[i], preferences, first: i == 0, compact: accounts.Count > 1,
                inIcon: icons.Where(icon => icon.Account is { } shown && shown == accounts[i]).Select(icon => (ResetKind?)icon.Window).FirstOrDefault(),
                group: icons.Count > 1 ? "TrayQuota" + i : "TrayQuota");
        }
    }

    private sealed class AccountPeek : StackPanel
    {
        private readonly Border _divider = Ui.Divider(new Thickness(0, 18, 0, 16));
        private readonly TextBlock _name = new() { FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _plan = new() { FontSize = 11, FontWeight = FontWeights.Medium };
        private readonly Border _planChip = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _weekly = new() { FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _short = new() { FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center };
        private readonly QuotaRing _weeklyRing = new() { Thickness = 6 };
        private readonly QuotaRing _shortRing = new() { Thickness = 6 };
        private readonly Grid _quotas = new();
        private readonly StackPanel _weeklyMetric, _shortMetric;
        private readonly TextBlock _weeklyReset = new() { FontSize = 12, FontWeight = FontWeights.Medium, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) };
        private readonly TextBlock _shortReset = new() { FontSize = 12, FontWeight = FontWeights.Medium, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) };
        private readonly Grid _weeklyResetRow, _shortResetRow;
        private readonly TextBlock _freshness = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly TextBlock _reserve = new() { FontSize = 12, FontWeight = FontWeights.Medium };
        private readonly TextBlock _expirations = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly StackPanel _reserves = new() { Margin = new Thickness(0, 0, 0, 12) };
        private readonly TextBlock _status = new() { FontSize = 11, FontWeight = FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        // Official icon of the tool, as on the dashboard's active account cards.
        private readonly Image _providerIcon = new() { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        private readonly RadioButton _weeklyChoice, _shortChoice;
        private readonly TextBlock _weeklyInIcon, _shortInIcon;
        private readonly bool _selectable;
        private AccountProvider? _provider;

        public AccountPeek(Action<AccountProvider, ResetKind>? select)
        {
            _selectable = select is not null;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); Muted(_plan); Muted(_freshness); Muted(_expirations);
            var status = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            status.Children.Add(_providerIcon); status.Children.Add(_status);
            Children.Add(_divider); Children.Add(status);
            var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            _name.Margin = new Thickness(0, 0, 8, 0); _name.VerticalAlignment = VerticalAlignment.Center;
            _planChip.Child = _plan; _planChip.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
            heading.Children.Add(_name); Grid.SetColumn(_planChip, 1); heading.Children.Add(_planChip); Children.Add(heading);
            _quotas.ColumnDefinitions.Add(new()); _quotas.ColumnDefinitions.Add(new());
            _weeklyMetric = Metric(Loc.T("Semaine"), _weekly, _weeklyRing, out _weeklyInIcon);
            _shortMetric = Metric(Loc.T("5 heures"), _short, _shortRing, out _shortInIcon);
            _weeklyChoice = Choice(_weeklyMetric, ResetKind.Weekly, select); _shortChoice = Choice(_shortMetric, ResetKind.Short, select);
            _quotas.Children.Add(_weeklyChoice); Grid.SetColumn(_shortChoice, 1); _quotas.Children.Add(_shortChoice); Children.Add(_quotas);
            _reserves.Children.Add(_reserve); _reserves.Children.Add(_expirations); Children.Add(_reserves);
            ToolTipService.SetInitialShowDelay(_reserve, 150);
            ToolTipService.SetShowDuration(_reserve, 60000);
            _shortResetRow = ResetRow(Loc.T("Reset 5 h"), _shortReset, "ShortResetMetric");
            _weeklyResetRow = ResetRow(Loc.T("Reset semaine"), _weeklyReset, "WeeklyResetMetric");
            Children.Add(_shortResetRow); Children.Add(_weeklyResetRow); Children.Add(_freshness);
        }

        private static Grid ResetRow(string label, TextBlock value, string tag)
        {
            var row = new Grid { Tag = tag, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var name = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            Muted(name); row.Children.Add(name); Grid.SetColumn(value, 1); row.Children.Add(value);
            ToolTipService.SetShowDuration(row, 60000);
            return row;
        }

        private static StackPanel Metric(string name, TextBlock number, QuotaRing ring, out TextBlock inIcon)
        {
            ring.SetResourceReference(QuotaRing.TrackBrushProperty, "TrackBrush");
            ring.SetResourceReference(QuotaRing.MarkerBrushProperty, "TextBrush");
            var gauge = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            number.VerticalAlignment = VerticalAlignment.Center; gauge.Children.Add(ring); gauge.Children.Add(number);
            var label = new TextBlock { Text = name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) }; Muted(label);
            inIcon = new TextBlock { Text = Loc.T("Dans l’icône"), FontSize = 11, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
            inIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            var panel = new StackPanel(); panel.Children.Add(gauge); panel.Children.Add(label); panel.Children.Add(inIcon); return panel;
        }

        // One choice across both tools: the checked quota is the one drawn in the notification icon.
        private RadioButton Choice(StackPanel metric, ResetKind kind, Action<AccountProvider, ResetKind>? select)
        {
            var choice = new RadioButton { Content = metric, GroupName = "TrayQuota", Tag = kind == ResetKind.Short ? "ShortQuotaChoice" : "WeeklyQuotaChoice" };
            choice.SetResourceReference(StyleProperty, "QuotaChoice");
            ToolTipService.SetInitialShowDelay(choice, 400);
            choice.Click += (_, _) => { if (_provider is { } provider) select?.Invoke(provider, kind); };
            return choice;
        }

        public void Update(TrackerState state, AccountState? account, TrackerPreferences preferences, bool first, bool compact, ResetKind? inIcon, string group)
        {
            var snapshot = account?.Snapshot;
            var now = PreviewClock.UtcNow;
            _divider.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
            // Two accounts stay readable without a scroll bar on a small screen.
            foreach (var ring in new[] { _weeklyRing, _shortRing }) ring.Width = ring.Height = compact ? 68 : 84;
            _weekly.FontSize = _short.FontSize = compact ? 17 : 19;
            _quotas.Margin = compact ? new Thickness(0, 12, 0, 12) : new Thickness(0, 18, 0, 18);
            var showShort = account?.HasShortWindow != false;
            _shortChoice.Visibility = showShort ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(_weeklyChoice, showShort ? 1 : 2);
            // A lone weekly quota keeps the tile width of a two-quota row.
            _weeklyChoice.HorizontalAlignment = showShort ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            _weeklyChoice.MinWidth = showShort ? 0 : 132;
            _provider = account?.Profile.Provider;
            foreach (var (choice, label, kind) in new[] { (_weeklyChoice, _weeklyInIcon, ResetKind.Weekly), (_shortChoice, _shortInIcon, ResetKind.Short) })
            {
                var selected = inIcon == kind;
                choice.GroupName = group;
                var pace = account is null ? null : QuotaPace.For(state, account, kind, PreviewClock.UtcNow);
                (kind == ResetKind.Weekly ? _weeklyRing : _shortRing).Marker = pace?.Expected ?? double.NaN;
                choice.IsChecked = selected; choice.IsEnabled = account is not null && _selectable;
                label.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                choice.ToolTip = account is null ? null : (selected ? Loc.T("Affiché dans l’icône de la barre des tâches") : Loc.T("Cliquez pour l’afficher dans l’icône de la barre des tâches"))
                    + (pace is null ? "" : "\n" + PaceDisplay.Line(pace));
                var provider = account?.Profile.ProviderName ?? "Codex Tracker";
                System.Windows.Automation.AutomationProperties.SetName(choice, kind == ResetKind.Short ? Loc.F("{0} · 5 heures", provider) : Loc.F("{0} · semaine", provider));
            }
            var weekly = account is null ? null : QuotaPresentation.Remaining(state, account, ResetKind.Weekly, now);
            var shortWindow = account is null ? null : QuotaPresentation.Remaining(state, account, ResetKind.Short, now);
            var declared = account is not null && state.ManualCodexReset?.Applies(account, ResetKind.Weekly, now) == true;
            // A declared reset restarts the windows: accounts without a new reading show the projected, estimated date.
            var (nextReset, weeklyProjected) = account is null ? (null, false) : QuotaPresentation.ShownResetsAt(state, account, ResetKind.Weekly, now);
            var (shortReset, shortProjected) = account is null ? (null, false) : QuotaPresentation.ShownResetsAt(state, account, ResetKind.Short, now);
            var shortDeclared = account is not null && state.ManualCodexReset?.Applies(account, ResetKind.Short, now) == true;
            _name.Text = account is null ? Loc.T("En attente d’un compte") : PrivacyText.ContextualAccount(account.Profile, state, preferences);
            _plan.Text = snapshot?.PlanType?.ToLowerInvariant() switch { "pro" or "prolite" => "Pro", "plus" => "Plus", "free" => "Free", null => "", var other => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other) };
            if (snapshot?.PlanMultiplier is int multiplier) _plan.Text += $" {multiplier}×";
            _weekly.Text = Display.Percent(weekly); _short.Text = Display.Percent(shortWindow);
            _weeklyRing.Value = weekly ?? 0; _shortRing.Value = shortWindow ?? 0;
            _weekly.Foreground = Display.QuotaBrush(weekly); _short.Foreground = Display.QuotaBrush(shortWindow);
            _weeklyRing.RingBrush = declared ? Display.Green : Display.QuotaBarBrush(weekly); _shortRing.RingBrush = Display.QuotaBarBrush(shortWindow);
            _planChip.Visibility = string.IsNullOrEmpty(_plan.Text) ? Visibility.Collapsed : Visibility.Visible;
            _providerIcon.Visibility = account is null ? Visibility.Collapsed : Visibility.Visible;
            if (account is not null) _providerIcon.SetResourceReference(Image.SourceProperty, account.Profile.Provider == AccountProvider.ClaudeCode ? "ClaudeProviderIcon" : "CodexProviderIcon");
            _status.Text = declared ? Loc.T("Reset Codex déclaré") : account?.IsActive == true ? Loc.F("Compte actif dans {0}", account.Profile.ProviderName) : Loc.T("Compte affiché dans l’icône");
            _shortResetRow.Visibility = showShort ? Visibility.Visible : Visibility.Collapsed;
            _weeklyResetRow.Margin = showShort ? new Thickness(0, 5, 0, 0) : new Thickness();
            _shortReset.Text = shortDeclared && !shortProjected ? Loc.T("À reconfirmer") : Display.ResetTiming(shortReset, shortProjected);
            _weeklyReset.Text = declared && !weeklyProjected ? Loc.T("À reconfirmer") : Display.ResetTiming(nextReset,
                weeklyProjected || snapshot?.Weekly is { IsResetEstimated: true } inferred && inferred.ResetsAt == nextReset);
            var declaredHint = state.ManualCodexReset is { } manual ? Loc.F("Reset Codex déclaré le {0} · {1}", Display.Exact(manual.At), Display.Zone(manual.At)) : "";
            string ProjectedHint(DateTimeOffset? at) => declaredHint + (at is { } projected ? "\n" + Loc.F("Prochain reset estimé : {0} · {1}", Display.Exact(projected), Display.Zone(projected)) : "");
            _shortResetRow.ToolTip = shortDeclared ? ProjectedHint(shortProjected ? shortReset : null) : Display.Exact(shortReset) + " · " + Display.Zone(shortReset);
            _weeklyResetRow.ToolTip = declared ? ProjectedHint(weeklyProjected ? nextReset : null) : snapshot?.Weekly is { IsResetEstimated: true } estimate && estimate.ResetsAt == nextReset
                ? Display.EstimatedReset(estimate) : Display.Exact(nextReset) + " · " + Display.Zone(nextReset);
            _reserve.Text = "↺ " + Display.ReserveSummary(snapshot);
            _reserve.ToolTip = new ToolTip { Content = new TextBlock { Text = Display.ReserveHint(snapshot, preferences.PrivacyMode), TextWrapping = TextWrapping.Wrap, MaxWidth = 390 } };
            _expirations.Text = snapshot?.ResetCredits is { Count: > 0 } credits
                ? string.Join("\n", credits.OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).Select(c =>
                    c.ExpiresAt is { } expires ? Display.Expiry(expires, DateTimeOffset.UtcNow) + " · " + Display.Zone(expires) : Loc.T("Expiration non communiquée")))
                : Loc.T("Dates d’expiration non communiquées");
            _reserves.Visibility = account?.Profile.Provider != AccountProvider.ClaudeCode ? Visibility.Visible : Visibility.Collapsed;
            if (snapshot is null) _freshness.Text = Loc.T("Ouvrez Codex ou Claude Code pour détecter votre compte.");
            else
            {
                var age = DateTimeOffset.UtcNow - snapshot.FetchedAt;
                var ageText = age.TotalMinutes < 1 ? Loc.T("à l’instant") : age.TotalHours < 1 ? Loc.F("il y a {0} min", (int)age.TotalMinutes) : Loc.F("le {0:dd/MM à HH:mm}", snapshot.FetchedAt.ToLocalTime());
                _freshness.Text = account?.IsActive != true ? Loc.F("Dernier relevé {0}", ageText) : account.IsStale ? Loc.F("Données anciennes · {0}", ageText) : Loc.F("Actualisé {0}", ageText);
            }
            _freshness.ToolTip = snapshot is null ? null : Display.Exact(snapshot.FetchedAt) + " · " + Display.Zone(snapshot.FetchedAt);
        }
    }
    private static void Muted(TextBlock value) => value.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

    public void ShowNear(DrawingPoint cursor)
    {
        if (_closed) return;
        _anchor = cursor;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // Place the HWND on the destination monitor before measuring its physical size.
        WindowsLifecycle.MoveToMonitor(hwnd, new(cursor.X, cursor.Y));
        FitToMonitor();
        Show(); UpdateLayout(); Position();
        QueuePosition();
    }
    private void QueuePosition()
    {
        if (_closed || _positionQueued || !IsVisible) return;
        _positionQueued = true;
        Dispatcher.InvokeAsync(() => { _positionQueued = false; if (!_closed && IsVisible) Position(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void FitToMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var work = WindowsLifecycle.WorkArea(new PixelPoint(_anchor.X, _anchor.Y));
        var scale = WindowsLifecycle.Scale(handle);
        MaxWidth = Math.Max(1, work.Width / scale - 20); MaxHeight = Math.Max(1, work.Height / scale - 20);
        Width = Math.Min(322, MaxWidth);
    }
    private void Position()
    {
        if (_closed || !IsVisible || _positioning || _offscreen) return;
        _positioning = true;
        try
        {
        var hwnd = new WindowInteropHelper(this).Handle;
        FitToMonitor(); UpdateLayout();
        var work = WindowsLifecycle.WorkArea(new PixelPoint(_anchor.X, _anchor.Y));
        var scale = WindowsLifecycle.Scale(hwnd);
        var bounds = WindowPlacement.Peek(new(_anchor.X, _anchor.Y), WindowPlacement.ToPixels(ActualWidth, ActualHeight, scale), work, scale);
        WindowsLifecycle.Move(hwnd, bounds, topmost: true);
        }
        finally { _positioning = false; }
    }

    public bool ContainsScreenPoint(DrawingPoint point)
    {
        if (!IsVisible || WindowsLifecycle.Bounds(new WindowInteropHelper(this).Handle) is not PixelRect bounds) return false;
        return point.X >= bounds.X - 5 && point.X <= bounds.Right + 5 && point.Y >= bounds.Y - 5 && point.Y <= bounds.Bottom + 5;
    }

    /// <summary>Documentation capture rendered off-screen: nothing appears near the cursor.</summary>
    internal void SaveOffscreenScreenshot(string path, double dpi = 96)
    {
        _offscreen = true;
        try { WindowStartupLocation = WindowStartupLocation.Manual; Left = Top = -32000; Show(); UpdateLayout(); SaveScreenshot(path, dpi); Hide(); }
        finally { _offscreen = false; }
    }

    internal void SaveScreenshot(string path, double dpi = 96)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi / 96), (int)Math.Ceiling(ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); encoder.Save(file);
    }

    private const int ExtendedStyle = -20;
    private const nint NoActivate = 0x08000000;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);
}
