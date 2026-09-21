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
        var panel = new Border { Child = child, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = padding ?? new Thickness(15) };
        panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); panel.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return panel;
    }
    public static void SaveScreenshot(Window window, string path, double dpi)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi / 96), (int)Math.Ceiling(window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var stream = File.Create(path); encoder.Save(stream);
    }
}

internal class ThemedWindow : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(21, 17, 21, 22) };
    protected readonly TextBlock Heading;
    protected readonly ScrollViewer ContentScroll;
    private readonly ThemeManager _theme;
    public ThemedWindow(Window owner, string title, ThemeManager theme, double width, double height)
    {
        Owner = owner; _theme = theme; Title = title; Width = width; Height = height;
        MinWidth = Math.Min(width, 440); MinHeight = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 46, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0) });
        var frame = new Border { BorderThickness = new Thickness(1) }; frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) }); root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(20, 0, 7, 0) };
        Heading = Ui.Text(title, 13); Heading.FontWeight = FontWeights.SemiBold; Heading.VerticalAlignment = VerticalAlignment.Center; Heading.Margin = new Thickness(0, 0, 40, 0);
        var close = new Button { Content = "✕", ToolTip = "Fermer", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(12, 6, 12, 6), Style = (Style)FindResource("QuietButton") };
        WindowChrome.SetIsHitTestVisibleInChrome(close, true); close.Click += (_, _) => Close();
        header.Children.Add(Heading); header.Children.Add(close);
        var top = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }; top.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); top.SetResourceReference(Border.BackgroundProperty, "ChromeBrush"); root.Children.Add(top);
        ContentScroll = new ScrollViewer { Content = Body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(ContentScroll, 1); root.Children.Add(ContentScroll);
        frame.Child = root; Content = frame;
        SourceInitialized += (_, _) => { Ui.ConstrainInitialSize(this); ApplyChrome(); }; theme.Changed += ThemeChanged;
        Loaded += (_, _) => Ui.EnsureWindowVisible(this);
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
