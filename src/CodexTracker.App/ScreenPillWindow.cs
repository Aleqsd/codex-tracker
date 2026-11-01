using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CodexTracker.App;

/// <summary>
/// A small pill above the clock while an account in use is nearly empty or a reset credit expires within 24 hours.
/// Visible even in Do Not Disturb, never activated (the keyboard stays in Codex), hidden over full-screen apps.
/// Hover shows the detail; "Masquer" hides this situation until another one arises.
/// </summary>
internal sealed class ScreenPillWindow : Window
{
    private readonly Action<string> _open;
    private readonly Action<string> _dismiss;
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
    private readonly TextBlock _summary = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
    private readonly TextBlock _account = new() { FontSize = 12, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _advice = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
    private readonly TextBlock _more = new() { FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    private readonly StackPanel _details = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8), MaxWidth = 300 };
    private readonly Border _frame = new() { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(10, 7, 14, 7) };
    private readonly System.Windows.Threading.DispatcherTimer _collapse = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private PillSituation? _shown;
    private bool _offscreen;

    internal string? ShownKey => _shown?.Key;
    internal bool IsExpanded => _details.Visibility == Visibility.Visible;

    /// <param name="open">Opens the tracker on a page (Comptes or Resets).</param>
    /// <param name="dismiss">Remembers that this situation was hidden.</param>
    public ScreenPillWindow(Action<string> open, Action<string> dismiss)
    {
        _open = open; _dismiss = dismiss;
        SetResourceReference(StyleProperty, typeof(Window));
        Title = Loc.T("Pastille Codex Tracker");
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        _frame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _advice.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _more.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");

        var open_ = new Button { Content = Loc.T("Ouvrir"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0) };
        open_.Click += (_, _) => { if (_shown is { } s) _open(s.Kind == PillKind.ExpiringReserve ? "Resets" : "Comptes"); };
        var hide = new Button { Content = Loc.T("Masquer"), Padding = new Thickness(10, 4, 10, 4) };
        hide.SetResourceReference(StyleProperty, "QuietButton");
        hide.ToolTip = Loc.T("Masquer jusqu’à la prochaine alerte");
        hide.Click += (_, _) => Dismiss();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(open_); actions.Children.Add(hide);
        _details.Children.Add(_account); _details.Children.Add(_detail); _details.Children.Add(_advice); _details.Children.Add(_more); _details.Children.Add(actions);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_dot); row.Children.Add(_summary);
        var content = new StackPanel(); content.Children.Add(_details); content.Children.Add(row);
        _frame.Child = content;
        Content = new Border { Padding = new Thickness(6), Child = _frame };
        System.Windows.Automation.AutomationProperties.SetName(this, Title);

        // Hover opens the detail; a click on the pill itself opens the tracker.
        _frame.MouseEnter += (_, _) => { _collapse.Stop(); Expand(true); };
        _frame.MouseLeave += (_, _) => _collapse.Start();
        _collapse.Tick += (_, _) => { _collapse.Stop(); Expand(false); };
        row.MouseLeftButtonUp += (_, e) => { if (_shown is { } s) { _open(s.Kind == PillKind.ExpiringReserve ? "Resets" : "Comptes"); e.Handled = true; } };
        row.Cursor = Cursors.Hand;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(hwnd, ExtendedStyle, GetWindowLongPtr(hwnd, ExtendedStyle) | NoActivate | ToolWindow);
        };
        SizeChanged += (_, _) => Place();
    }

    private void Expand(bool expanded)
    {
        _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _frame.CornerRadius = new CornerRadius(expanded ? 12 : 18);
        _frame.Padding = expanded ? new Thickness(14, 12, 14, 10) : new Thickness(10, 7, 14, 7);
    }

    private void Dismiss()
    {
        if (_shown is not { } situation) return;
        _dismiss(situation.Key); _shown = null; Expand(false); Hide();
    }

    public void Update(TrackerState state, TrackerPreferences preferences, DateTimeOffset now)
    {
        state = state with { ManualCodexReset = preferences.ManualCodexReset };
        var situations = preferences.ScreenPill ? ScreenPill.Current(state, now, preferences.PillHidden) : [];
        // Never over a full-screen game, video or presentation.
        if (situations.Count == 0 || !_offscreen && FullScreenInFront()) { _shown = null; if (IsVisible) Hide(); return; }
        var situation = situations[0];
        _shown = situation;
        var name = PrivacyText.Account(situation.Account.Profile, state, preferences);
        var provider = situation.Account.Profile.ProviderName;
        var hour = situation.At is { } at ? Display.ResetHour(at).ToLowerInvariant() : null;
        if (situation.Kind == PillKind.LowQuota)
        {
            _dot.SetResourceReference(Shape.FillProperty, "DangerBrush");
            var percent = Math.Floor(situation.Remaining ?? 0);
            _summary.Text = hour is null ? Loc.F("{0} · {1:0} %", provider, percent) : Loc.F("{0} · {1:0} % · recharge {2}", provider, percent, hour);
            _account.Text = name + " · " + provider;
            _detail.Text = Loc.F("Il reste {0:0} % {1}.", percent, situation.Window == ResetKind.Short ? Loc.T("sur 5 heures") : Loc.T("cette semaine"));
            var advice = NotificationPolicy.Suggestion(state, situation.Account, preferences, now);
            _advice.Text = advice ?? ""; _advice.Visibility = advice is null ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            _dot.SetResourceReference(Shape.FillProperty, "WarningBrush");
            _summary.Text = Loc.F("Réserve à utiliser avant {0}", hour ?? "");
            _account.Text = name + " · " + provider;
            _detail.Text = Loc.T("Un reset en réserve expire dans moins de 24 h : utilisez-le dans Codex avant qu’il soit perdu.");
            _advice.Visibility = Visibility.Collapsed;
        }
        _more.Text = situations.Count > 1 ? Loc.F("+{0} autre alerte", situations.Count - 1) : "";
        _more.Visibility = situations.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetHelpText(this, _summary.Text);
        if (!IsVisible) { Show(); Place(); }
    }

    /// <summary>Bottom right of the primary work area, just above the clock.</summary>
    private void Place()
    {
        if (_offscreen || !IsLoaded) return;
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 6;
        Top = work.Bottom - ActualHeight - 6;
    }

    /// <summary>Documentation capture rendered off-screen, expanded or not: nothing appears above the clock.</summary>
    internal bool SaveOffscreenScreenshot(TrackerState state, TrackerPreferences preferences, DateTimeOffset now, string path, bool expanded, double dpi = 96)
    {
        _offscreen = true;
        try
        {
            Left = Top = -32000; Update(state, preferences, now);
            if (!IsVisible) return false;
            Expand(expanded); UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi / 96), (int)Math.Ceiling(ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using var file = File.Create(path); encoder.Save(file);
            Expand(false); Hide(); return true;
        }
        finally { _offscreen = false; }
    }

    /// <summary>For tests: what a click on "Masquer" does.</summary>
    internal void ClickHide() => Dismiss();

    private static bool FullScreenInFront()
    {
        // 2 busy (full screen), 3 Direct3D full screen, 4 presentation mode.
        try { return SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4; }
        catch (Exception) { return false; }
    }

    private const int ExtendedStyle = -20;
    private const nint NoActivate = 0x08000000, ToolWindow = 0x00000080;
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtr(IntPtr hwnd, int index, nint value);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
