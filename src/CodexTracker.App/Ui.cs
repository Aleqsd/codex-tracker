using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace CodexTracker.App;

internal static class Ui
{
    public static void ConstrainInitialSize(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var bounds = WindowsLifecycle.Bounds(handle); if (bounds is null) return;
        var area = WindowsLifecycle.WorkArea(bounds.Value); var scale = WindowsLifecycle.Scale(handle);
        double width = Math.Max(1, area.Width / scale - 24);
        double height = Math.Max(1, area.Height / scale - 24);
        window.MinWidth = Math.Min(window.MinWidth, width); window.MinHeight = Math.Min(window.MinHeight, height);
        window.Width = Math.Min(window.Width, width); window.Height = Math.Min(window.Height, height);
    }
    public static void EnsureWindowVisible(Window window)
    {
        // Preserve minimized/maximized state; ShowPanel calls this after restoration.
        if (window.WindowState != WindowState.Normal) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || WindowsLifecycle.Bounds(handle) is not PixelRect bounds) return;
        var work = WindowsLifecycle.WorkArea(bounds); var scale = WindowsLifecycle.Scale(handle);
        var fitted = WindowPlacement.Constrain(bounds, work, (int)Math.Ceiling(12 * scale));
        window.MinWidth = Math.Min(window.MinWidth, fitted.Width / scale);
        window.MinHeight = Math.Min(window.MinHeight, fitted.Height / scale);
        if (fitted != bounds) WindowsLifecycle.Move(handle, fitted);
    }
    public static void HandleEnvironmentChanged(Window root)
    {
        if (root.Dispatcher.HasShutdownStarted) return;
        EnsureWindowVisible(root);
        foreach (Window child in root.OwnedWindows.Cast<Window>().ToArray()) HandleEnvironmentChanged(child);
    }
    public static TextBlock Text(string text, double size = 12, string brush = "TextBrush")
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush); return block;
    }
    public static Border Panel(UIElement child, Thickness? padding = null)
    {
        var panel = new Border { Child = child, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(16) };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); panel.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return panel;
    }
    public static Border Divider(Thickness margin)
    {
        var line = new Border { Height = 1, Margin = margin }; line.SetResourceReference(Border.BackgroundProperty, "LineBrush"); return line;
    }
    public static Viewbox Icon(string geometry, double size, string brush = "MutedBrush", double thickness = 1.8)
    {
        var path = new System.Windows.Shapes.Path { Data = (Geometry)System.Windows.Application.Current.FindResource(geometry), StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        return new Viewbox { Child = canvas, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }
    /// <summary>A quiet square button whose glyph follows the button foreground.</summary>
    public static Button IconButton(string geometry, string tooltip, string automationName, double size = 16, double thickness = 1.8)
    {
        var path = new System.Windows.Shapes.Path { Data = (Geometry)System.Windows.Application.Current.FindResource(geometry), StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(path);
        var button = new Button { Content = new Viewbox { Child = canvas, Width = size, Height = size }, ToolTip = tooltip, Style = (Style)System.Windows.Application.Current.FindResource("IconButton") };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = button });
        System.Windows.Automation.AutomationProperties.SetName(button, automationName); return button;
    }
    public static void SaveScreenshot(Window window, string path, double dpi)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96), (int)Math.Ceiling(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var stream = File.Create(path); encoder.Save(stream);
    }
}

/// <summary>Circular quota gauge drawn from theme brushes; the percentage remains a separate TextBlock.</summary>
public sealed class QuotaRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(QuotaRing),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(nameof(RingBrush), typeof(Brush), typeof(QuotaRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(QuotaRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(QuotaRing),
        new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));
    // Presentation only: the arc sweeps in when shown, the percentage text is always the measured value.
    private static readonly DependencyProperty SweepProperty = DependencyProperty.Register("Sweep", typeof(double), typeof(QuotaRing),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush? RingBrush { get => (Brush?)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public QuotaRing()
    {
        Loaded += (_, _) => SweepIn();
        IsVisibleChanged += (_, _) => { if (IsVisible) SweepIn(); else BeginAnimation(SweepProperty, null); };
        Unloaded += (_, _) => BeginAnimation(SweepProperty, null);
    }
    private void SweepIn()
    {
        if (!IsLoaded || !IsVisible || !UiMotion.Allowed(this)) return;
        BeginAnimation(SweepProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(950))
        {
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
            EasingFunction = new System.Windows.Media.Animation.QuinticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var radius = (size - Thickness) / 2; var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (TrackBrush is { } track) dc.DrawEllipse(null, new Pen(track, Thickness), center, radius, radius);
        var value = (double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) : 0) * (double)GetValue(SweepProperty);
        if (value <= 0 || RingBrush is null) return;
        var pen = new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 99.95) { dc.DrawEllipse(null, pen, center, radius, radius); return; }
        var angle = value * 3.6;
        Point At(double degrees)
        {
            var radians = (degrees - 90) * Math.PI / 180;
            return new(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
        }
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(At(0), false, false);
            context.ArcTo(At(angle), new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze(); dc.DrawGeometry(null, pen, arc);
    }
}

internal class ThemedWindow : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(24, 18, 24, 24) };
    protected readonly TextBlock Heading;
    protected readonly ScrollViewer ContentScroll;
    private readonly ThemeManager _theme;
    private readonly bool _offscreen;
    public ThemedWindow(Window owner, string title, ThemeManager theme, double width, double height)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner; _theme = theme; Title = title; Width = width; Height = height;
        // A capture from an off-screen preview stays off-screen too.
        _offscreen = owner is MainWindow { Offscreen: true };
        MinWidth = Math.Min(width, 440); MinHeight = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 50, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0) });
        var frame = new Border { BorderThickness = new Thickness(1) }; frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) }); root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(24, 0, 8, 0) };
        Heading = Ui.Text(title, 14); Heading.FontWeight = FontWeights.Medium; Heading.VerticalAlignment = VerticalAlignment.Center; Heading.Margin = new Thickness(0, 0, 44, 0);
        var close = Ui.IconButton("CloseIcon", Loc.T("Fermer"), Loc.T("Fermer"), 15); close.Style = (Style)FindResource("ChromeCloseButton");
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Center;
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += (_, _) => Close();
        header.Children.Add(Heading); header.Children.Add(close);
        var top = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }; top.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); root.Children.Add(top);
        ContentScroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(ContentScroll, 1); root.Children.Add(ContentScroll);
        UiMotion.SetRise(root, 10); UiMotion.SetFadeOnShow(root, true);
        frame.Child = root; Content = frame;
        SourceInitialized += (_, _) => { Ui.ConstrainInitialSize(this); ApplyChrome(); }; theme.Changed += ThemeChanged;
        if (_offscreen) { WindowStartupLocation = WindowStartupLocation.Manual; Left = Top = -32000; ShowActivated = false; }
        Loaded += (_, _) => { if (!_offscreen) Ui.EnsureWindowVisible(this); };
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(() => Ui.EnsureWindowVisible(this), System.Windows.Threading.DispatcherPriority.Loaded);
        Closed += (_, _) => theme.Changed -= ThemeChanged;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
    private void ThemeChanged(object? sender, EventArgs e) => ApplyChrome();
    private void ApplyChrome()
    {
        var handle = new WindowInteropHelper(this).Handle; if (handle == IntPtr.Zero) return;
        int dark = _theme.IsDark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref dark, 4); int rounded = 2; DwmSetWindowAttribute(handle, 33, ref rounded, 4);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
