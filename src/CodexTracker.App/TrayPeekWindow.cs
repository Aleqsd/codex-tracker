using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;

namespace CodexTracker.App;

// A non-activating window: a glance at the quota must not steal keyboard focus from Codex.
internal sealed class TrayPeekWindow : Window
{
    private readonly TextBlock _name = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _plan = new() { FontSize = 10 };
    private readonly TextBlock _weekly = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _short = new() { FontSize = 26, FontWeight = FontWeights.SemiBold };
    private readonly ProgressBar _weeklyBar = new() { Maximum = 100, Height = 3, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _shortBar = new() { Maximum = 100, Height = 3, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _reset = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 0) };
    private readonly TextBlock _freshness = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _reserve = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 0) };
    private readonly TextBlock _expirations = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _status = new() { FontSize = 10, Margin = new Thickness(0, 0, 0, 10) };
    private DrawingPoint _anchor;
    private bool _positionQueued, _positioning, _closed;

    public TrayPeekWindow(Action open)
    {
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
        var border = new Border { Padding = new Thickness(19), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var content = new StackPanel(); border.Child = content;
        Content = new ScrollViewer { Content = border, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Muted(_status); Muted(_plan); Muted(_reset); Muted(_freshness);
        content.Children.Add(_status);
        var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _name.Margin = new Thickness(0, 0, 8, 0); _plan.VerticalAlignment = VerticalAlignment.Center;
        heading.Children.Add(_name); Grid.SetColumn(_plan, 1); heading.Children.Add(_plan); content.Children.Add(heading);
        var quotas = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        quotas.ColumnDefinitions.Add(new()); quotas.ColumnDefinitions.Add(new());
        var weekly = Metric("Semaine", _weekly, _weeklyBar); weekly.Margin = new Thickness(0, 0, 15, 0);
        var shortWindow = Metric("5 heures", _short, _shortBar); shortWindow.Margin = new Thickness(15, 0, 0, 0);
        quotas.Children.Add(weekly); Grid.SetColumn(shortWindow, 1); quotas.Children.Add(shortWindow); content.Children.Add(quotas);
        content.Children.Add(_reserve); content.Children.Add(_expirations);
        Muted(_expirations);
        ToolTipService.SetInitialShowDelay(_reserve, 150);
        ToolTipService.SetShowDuration(_reserve, 60000);
        content.Children.Add(_reset); content.Children.Add(_freshness);
        var button = new Button { Content = "Ouvrir le suivi", Margin = new Thickness(0, 17, 0, 0), Padding = new Thickness(10, 7, 10, 7), HorizontalAlignment = HorizontalAlignment.Stretch };
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

    private static StackPanel Metric(string name, TextBlock number, ProgressBar bar)
    {
        var panel = new StackPanel();
        var label = new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(0, 0, 3, 3) }; Muted(label);
        panel.Children.Add(label); panel.Children.Add(number); panel.Children.Add(bar); return panel;
    }
    private static void Muted(TextBlock value) => value.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

    public void Update(TrackerState state, TrackerPreferences preferences)
    {
        var account = state.SelectedAccount;
        var snapshot = account?.Snapshot;
        var weekly = snapshot?.Weekly;
        var shortWindow = snapshot?.Buckets.FirstOrDefault(b => b.Id == "codex")?.Windows.FirstOrDefault(w => w.WindowDurationMins == 300);
        _name.Text = account is null ? "En attente de Codex" : PrivacyText.Account(account.Profile, state, preferences.PrivacyMode);
        _plan.Text = snapshot?.PlanType?.ToLowerInvariant() switch { "pro" or "prolite" => "Pro", "plus" => "Plus", "free" => "Free", null => "", var other => other };
        if (snapshot?.PlanMultiplier is int multiplier) _plan.Text += $" {multiplier}×";
        _weekly.Text = Display.Percent(weekly?.RemainingPercent); _short.Text = Display.Percent(shortWindow?.RemainingPercent);
        _weeklyBar.Value = weekly?.RemainingPercent ?? 0; _shortBar.Value = shortWindow?.RemainingPercent ?? 0;
        _weeklyBar.Foreground = Display.QuotaBrush(weekly?.RemainingPercent); _shortBar.Foreground = Display.QuotaBrush(shortWindow?.RemainingPercent);
        _status.Text = account?.IsActiveInCodex == true ? "COMPTE ACTIF DANS CODEX" : "COMPTE AFFICHÉ DANS L’ICÔNE";
        _reset.Text = weekly?.ResetsAt is null ? "Reset hebdomadaire indisponible" : "Reset · " + Display.Countdown(weekly.ResetsAt);
        _reset.ToolTip = Display.Exact(weekly?.ResetsAt) + " · " + Display.Zone(weekly?.ResetsAt);
        _reserve.Text = "↺ " + Display.ReserveSummary(snapshot);
        _reserve.ToolTip = new ToolTip { Content = new TextBlock { Text = Display.ReserveHint(snapshot, preferences.PrivacyMode), TextWrapping = TextWrapping.Wrap, MaxWidth = 390 } };
        _expirations.Text = snapshot?.ResetCredits is { Count: > 0 } credits
            ? string.Join("\n", credits.OrderBy(c => c.ExpiresAt ?? DateTimeOffset.MaxValue).Select(c =>
                c.ExpiresAt is { } expires ? $"{(expires <= DateTimeOffset.UtcNow ? "Expiration passée" : "Expire le")} {Display.Exact(expires)} · {Display.Zone(expires)}" : "Expiration non communiquée"))
            : "Dates d’expiration non communiquées";
        if (snapshot is null) _freshness.Text = "Ouvrez votre compte dans Codex pour le détecter.";
        else
        {
            var age = DateTimeOffset.UtcNow - snapshot.FetchedAt;
            var ageText = age.TotalMinutes < 1 ? "à l’instant" : age.TotalHours < 1 ? $"il y a {(int)age.TotalMinutes} min" : $"le {snapshot.FetchedAt.ToLocalTime():dd/MM à HH:mm}";
            _freshness.Text = account?.IsActiveInCodex != true ? "Dernier relevé " + ageText : account.IsStale ? "Données anciennes · " + ageText : "Actualisé " + ageText;
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
