using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;

namespace CodexTracker.App;

// A non-activating window: a glance at the quota must not steal keyboard focus from Codex.
internal sealed class TrayPeekWindow : Window
{
    private readonly TextBlock _name = new() { FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _plan = new() { FontSize = 11, FontWeight = FontWeights.Medium };
    private readonly Border _planChip = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _weekly = new() { FontSize = 19, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _short = new() { FontSize = 19, FontWeight = FontWeights.Medium, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly QuotaRing _weeklyRing = new() { Width = 84, Height = 84, Thickness = 6 };
    private readonly QuotaRing _shortRing = new() { Width = 84, Height = 84, Thickness = 6 };
    private readonly TextBlock _reset = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _freshness = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _reserve = new() { FontSize = 12, FontWeight = FontWeights.Medium };
    private readonly TextBlock _expirations = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly StackPanel _reserves = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _status = new() { FontSize = 11, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 0, 0, 8) };
    private DrawingPoint _anchor;
    private bool _positionQueued, _positioning, _closed;

    public TrayPeekWindow(Action open)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        // The shared window style applies the same smoothing as the main interface.
        Title = "Aperçu Codex Tracker";
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
        Content = new ScrollViewer { Content = border, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); Muted(_plan); Muted(_reset); Muted(_freshness); Muted(_expirations);
        content.Children.Add(_status);
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _name.Margin = new Thickness(0, 0, 8, 0); _name.VerticalAlignment = VerticalAlignment.Center;
        _planChip.Child = _plan; _planChip.SetResourceReference(Border.BackgroundProperty, "RaisedBrush");
        heading.Children.Add(_name); Grid.SetColumn(_planChip, 1); heading.Children.Add(_planChip); content.Children.Add(heading);
        var quotas = new Grid { Margin = new Thickness(0, 18, 0, 18) };
        quotas.ColumnDefinitions.Add(new()); quotas.ColumnDefinitions.Add(new());
        var weekly = Metric("Semaine", _weekly, _weeklyRing);
        var shortWindow = Metric("5 heures", _short, _shortRing);
        quotas.Children.Add(weekly); Grid.SetColumn(shortWindow, 1); quotas.Children.Add(shortWindow); content.Children.Add(quotas);
        _reserves.Children.Add(_reserve); _reserves.Children.Add(_expirations); content.Children.Add(_reserves);
        ToolTipService.SetInitialShowDelay(_reserve, 150);
        ToolTipService.SetShowDuration(_reserve, 60000);
        content.Children.Add(_reset); content.Children.Add(_freshness);
        var button = new Button { Content = "Ouvrir le suivi", Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(10, 8, 10, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
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

    private static StackPanel Metric(string name, TextBlock number, QuotaRing ring)
    {
        ring.SetResourceReference(QuotaRing.TrackBrushProperty, "TrackBrush");
        var gauge = new Grid { Width = ring.Width, Height = ring.Height, HorizontalAlignment = HorizontalAlignment.Center };
        number.VerticalAlignment = VerticalAlignment.Center; gauge.Children.Add(ring); gauge.Children.Add(number);
        var label = new TextBlock { Text = name, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) }; Muted(label);
        var panel = new StackPanel(); panel.Children.Add(gauge); panel.Children.Add(label); return panel;
    }
    private static void Muted(TextBlock value) => value.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

    public void Update(TrackerState state, TrackerPreferences preferences)
    {
        state = state with { ManualCodexReset = preferences.ManualCodexReset };
        var account = state.ActiveAccount;
        var snapshot = account?.Snapshot;
        var now = PreviewClock.UtcNow;
        var weekly = account is null ? null : QuotaPresentation.Remaining(state, account, ResetKind.Weekly, now);
        var shortWindow = account is null ? null : QuotaPresentation.Remaining(state, account, ResetKind.Short, now);
        var declared = account is not null && state.ManualCodexReset?.Applies(account, ResetKind.Weekly, now) == true;
        var nextReset = account is null ? null : QuotaPresentation.ResetsAt(state, account, ResetKind.Weekly, now);
        _name.Text = account is null ? "En attente d’un compte" : PrivacyText.ContextualAccount(account.Profile, state, preferences);
        _plan.Text = snapshot?.PlanType?.ToLowerInvariant() switch { "pro" or "prolite" => "Pro", "plus" => "Plus", "free" => "Free", null => "", var other => other };
        if (snapshot?.PlanMultiplier is int multiplier) _plan.Text += $" {multiplier}×";
        _weekly.Text = Display.Percent(weekly); _short.Text = Display.Percent(shortWindow);
        _weeklyRing.Value = weekly ?? 0; _shortRing.Value = shortWindow ?? 0;
        _weekly.Foreground = Display.QuotaBrush(weekly); _short.Foreground = Display.QuotaBrush(shortWindow);
        _weeklyRing.RingBrush = declared ? Display.Green : Display.QuotaBarBrush(weekly); _shortRing.RingBrush = Display.QuotaBarBrush(shortWindow);
        _planChip.Visibility = string.IsNullOrEmpty(_plan.Text) ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = declared ? "Reset Codex déclaré" : account?.IsActive == true ? $"Compte actif dans {account.Profile.ProviderName}" : "Compte affiché dans l’icône";
        _reset.Text = declared ? "Prochain reset à reconfirmer" : nextReset is null ? "Reset hebdomadaire indisponible" : "Reset · " + Display.Countdown(nextReset);
        _reset.ToolTip = declared ? $"Reset Codex déclaré le {Display.Exact(state.ManualCodexReset!.At)} · {Display.Zone(state.ManualCodexReset.At)}" : Display.Exact(nextReset) + " · " + Display.Zone(nextReset);
        _reserve.Text = "↺ " + Display.ReserveSummary(snapshot);
        _reserve.ToolTip = new ToolTip { Content = new TextBlock { Text = Display.ReserveHint(snapshot, preferences.PrivacyMode), TextWrapping = TextWrapping.Wrap, MaxWidth = 390 } };
        _expirations.Text = snapshot?.ResetCredits is { Count: > 0 } credits
            ? string.Join("\n", credits.OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).Select(c =>
                c.ExpiresAt is { } expires ? $"{(expires <= DateTimeOffset.UtcNow ? "Expiration passée" : "Expire le")} {Display.Exact(expires)} · {Display.Zone(expires)}" : "Expiration non communiquée"))
            : "Dates d’expiration non communiquées";
        var reservesVisible = account?.Profile.Provider != AccountProvider.ClaudeCode;
        _reserves.Visibility = reservesVisible ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is null) _freshness.Text = "Ouvrez Codex ou Claude Code pour détecter votre compte.";
        else
        {
            var age = DateTimeOffset.UtcNow - snapshot.FetchedAt;
            var ageText = age.TotalMinutes < 1 ? "à l’instant" : age.TotalHours < 1 ? $"il y a {(int)age.TotalMinutes} min" : $"le {snapshot.FetchedAt.ToLocalTime():dd/MM à HH:mm}";
            _freshness.Text = account?.IsActive != true ? "Dernier relevé " + ageText : account.IsStale ? "Données anciennes · " + ageText : "Actualisé " + ageText;
        }
        _freshness.ToolTip = snapshot is null ? null : Display.Exact(snapshot.FetchedAt) + " · " + Display.Zone(snapshot.FetchedAt);
    }

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
