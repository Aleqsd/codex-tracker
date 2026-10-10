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
    private DrawingPoint _anchor;
    private bool _positionQueued, _positioning, _closed;

    public TrayPeekWindow(Action open)
    {
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
        };
        DpiChanged += (_, _) => QueuePosition();
        SizeChanged += (_, _) => { if (!_positioning) QueuePosition(); };
        Closed += (_, _) => _closed = true;
    }

    public void Update(TrackerState state, TrackerPreferences preferences)
    {
        state = state with { ManualCodexReset = preferences.ManualCodexReset };
        IReadOnlyList<AccountState?> accounts = state.ActiveAccounts.Count > 0 ? [.. state.ActiveAccounts] : [null];
        while (_sections.Children.Count < accounts.Count) _sections.Children.Add(new AccountPeek());
        for (int i = 0; i < _sections.Children.Count; i++)
        {
            var section = (AccountPeek)_sections.Children[i];
            section.Visibility = i < accounts.Count ? Visibility.Visible : Visibility.Collapsed;
            if (i < accounts.Count) section.Update(state, accounts[i], preferences, first: i == 0, compact: accounts.Count > 1);
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
        private readonly TextBlock _reset = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _freshness = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly TextBlock _reserve = new() { FontSize = 12, FontWeight = FontWeights.Medium };
        private readonly TextBlock _expirations = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly StackPanel _reserves = new() { Margin = new Thickness(0, 0, 0, 12) };
        private readonly TextBlock _status = new() { FontSize = 11, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 8) };

        public AccountPeek()
        {
            _status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); Muted(_plan); Muted(_reset); Muted(_freshness); Muted(_expirations);
            Children.Add(_divider); Children.Add(_status);
            var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            _name.Margin = new Thickness(0, 0, 8, 0); _name.VerticalAlignment = VerticalAlignment.Center;
            _planChip.Child = _plan; _planChip.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
            heading.Children.Add(_name); Grid.SetColumn(_planChip, 1); heading.Children.Add(_planChip); Children.Add(heading);
            _quotas.ColumnDefinitions.Add(new()); _quotas.ColumnDefinitions.Add(new());
            _weeklyMetric = Metric(Loc.T("Semaine"), _weekly, _weeklyRing);
            _shortMetric = Metric(Loc.T("5 heures"), _short, _shortRing);
            _quotas.Children.Add(_weeklyMetric); Grid.SetColumn(_shortMetric, 1); _quotas.Children.Add(_shortMetric); Children.Add(_quotas);
            _reserves.Children.Add(_reserve); _reserves.Children.Add(_expirations); Children.Add(_reserves);
            ToolTipService.SetInitialShowDelay(_reserve, 150);
            ToolTipService.SetShowDuration(_reserve, 60000);
            Children.Add(_reset); Children.Add(_freshness);
        }

        private static StackPanel Metric(string name, TextBlock number, QuotaRing ring)
        {
            ring.SetResourceReference(QuotaRing.TrackBrushProperty, "TrackBrush");
            var gauge = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            number.VerticalAlignment = VerticalAlignment.Center; gauge.Children.Add(ring); gauge.Children.Add(number);
            var label = new TextBlock { Text = name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) }; Muted(label);
            var panel = new StackPanel(); panel.Children.Add(gauge); panel.Children.Add(label); return panel;
        }

        public void Update(TrackerState state, AccountState? account, TrackerPreferences preferences, bool first, bool compact)
        {
            var snapshot = account?.Snapshot;
            var now = PreviewClock.UtcNow;
            _divider.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
            // Two accounts stay readable without a scroll bar on a small screen.
            foreach (var ring in new[] { _weeklyRing, _shortRing }) ring.Width = ring.Height = compact ? 68 : 84;
            _weekly.FontSize = _short.FontSize = compact ? 17 : 19;
            _quotas.Margin = compact ? new Thickness(0, 12, 0, 12) : new Thickness(0, 18, 0, 18);
            var showShort = account?.HasShortWindow != false;
            _shortMetric.Visibility = showShort ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumnSpan(_weeklyMetric, showShort ? 1 : 2);
            var weekly = account is null ? null : QuotaPresentation.Remaining(state, account, ResetKind.Weekly, now);
            var shortWindow = account is null ? null : QuotaPresentation.Remaining(state, account, ResetKind.Short, now);
            var declared = account is not null && state.ManualCodexReset?.Applies(account, ResetKind.Weekly, now) == true;
            var nextReset = account is null ? null : QuotaPresentation.ResetsAt(state, account, ResetKind.Weekly, now);
            _name.Text = account is null ? Loc.T("En attente d’un compte") : PrivacyText.ContextualAccount(account.Profile, state, preferences);
            _plan.Text = snapshot?.PlanType?.ToLowerInvariant() switch { "pro" or "prolite" => "Pro", "plus" => "Plus", "free" => "Free", null => "", var other => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other) };
            if (snapshot?.PlanMultiplier is int multiplier) _plan.Text += $" {multiplier}×";
            _weekly.Text = Display.Percent(weekly); _short.Text = Display.Percent(shortWindow);
            _weeklyRing.Value = weekly ?? 0; _shortRing.Value = shortWindow ?? 0;
            _weekly.Foreground = Display.QuotaBrush(weekly); _short.Foreground = Display.QuotaBrush(shortWindow);
            _weeklyRing.RingBrush = declared ? Display.Green : Display.QuotaBarBrush(weekly); _shortRing.RingBrush = Display.QuotaBarBrush(shortWindow);
            _planChip.Visibility = string.IsNullOrEmpty(_plan.Text) ? Visibility.Collapsed : Visibility.Visible;
            _status.Text = declared ? Loc.T("Reset Codex déclaré") : account?.IsActive == true ? Loc.F("Compte actif dans {0}", account.Profile.ProviderName) : Loc.T("Compte affiché dans l’icône");
            _reset.Text = declared ? Loc.T("Prochain reset à reconfirmer") : nextReset is null ? Loc.T("Reset hebdomadaire indisponible")
                : "Reset · " + (snapshot?.Weekly is { IsResetEstimated: true } inferred && inferred.ResetsAt == nextReset ? "≈ " : "") + Display.Countdown(nextReset);
            _reset.ToolTip = declared ? Loc.F("Reset Codex déclaré le {0} · {1}", Display.Exact(state.ManualCodexReset!.At), Display.Zone(state.ManualCodexReset.At)) : Display.Exact(nextReset) + " · " + Display.Zone(nextReset);
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
        if (_closed || !IsVisible || _positioning) return;
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

    internal void SaveScreenshot(string path, double dpi = 96)
    {
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi / 96), (int)Math.Ceiling(ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); encoder.Save(file);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
