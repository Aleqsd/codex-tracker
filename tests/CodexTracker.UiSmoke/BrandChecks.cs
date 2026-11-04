using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CodexTracker.App;
using ThemeMode = CodexTracker.App.ThemeMode;

/// <summary>The owl logo and illustrations: decoded, tinted by the theme, decorative only, in the places they belong.</summary>
internal static class BrandChecks
{
    private static readonly string[] Mascots = ["MascotMain", "MascotWatching", "MascotSleeping", "MascotTerminal", "MascotReading", "MascotWindow"];
    private static void Check(bool result, string message) { if (!result) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Rectangle[] Owls(DependencyObject root, string key) =>
        Tree(root).OfType<Rectangle>().Where(r => ReferenceEquals(r.OpacityMask, Application.Current.FindResource(key))).ToArray();

    public static async Task Run(MainWindow window)
    {
        // The logo is the vector owl (assets/brand/owl.svg); the other owls are 256 px silhouettes.
        Check(Application.Current.FindResource("MascotMain") is DrawingBrush { Drawing: GeometryDrawing { Geometry.Bounds: { Width: > 500, Height: > 500 } } },
            "The owl logo is drawn from its vector path");
        foreach (var key in Mascots.Skip(1))
            Check(Application.Current.FindResource(key) is ImageBrush { ImageSource: BitmapSource { PixelWidth: 256, PixelHeight: 256 } }, $"{key} illustration decodes");
        var theme = window.Preferences.Current.ThemeMode;
        var (width, height) = (window.Width, window.Height);
        try
        {
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            foreach (var compact in new[] { false, true })
            {
                window.Preferences.Update(p => p with { ThemeMode = mode }); await Task.Delay(500);
                window.Width = compact ? 630 : 760; window.Height = compact ? 500 : 620;
                var name = $"{mode.ToString().ToLowerInvariant()}-{(compact ? "compact" : "normal")}";
                window.OpenPage("Comptes"); window.UpdateLayout();
                var mark = Owls(window, "MascotMain").FirstOrDefault(r => r.IsVisible && r.Parent is Border { CornerRadius.TopLeft: 7 });
                Check(mark is { IsHitTestVisible: false, Focusable: false } && mark.Fill == Application.Current.FindResource("PrimaryButtonTextBrush"),
                    $"The title bar shows the owl logo in the theme's colours ({name})");
                await Shown(window, "MascotWatching", $"artifacts/previews/brand-empty-{name}.png");
                await Shown(window, "MascotWindow", $"artifacts/previews/brand-onboarding-{name}.png");

                foreach (var (page, key) in new[] { ("Assistants", "MascotReading"), ("Application", "MascotMain"), ("Général", "MascotTerminal") })
                {
                    window.OpenPage(page); await Task.Delay(700); window.UpdateLayout();
                    var owl = Owls(Tree(window).OfType<SettingsView>().Single(), key).Single();
                    owl.BringIntoView(); window.UpdateLayout(); await Task.Delay(400);
                    var at = owl.TransformToAncestor(window).Transform(new Point());
                    Check(owl.IsVisible && owl.ActualWidth > 40 && at.X + owl.ActualWidth <= window.ActualWidth - 8 && !owl.IsHitTestVisible,
                        $"{page} settings show their owl inside the window ({name})");
                    window.SaveScreenshot(System.IO.Path.GetFullPath($"artifacts/previews/brand-{page}-{name}.png"), 96);
                }
            }
        }
        finally
        {
            window.Preferences.Update(p => p with { ThemeMode = theme }); window.Width = width; window.Height = height;
            window.OpenPage("Comptes"); window.UpdateLayout();
        }
    }

    /// <summary>A card shown only in a state the demo does not reach (no account, first launch): revealed for its capture, then bound again.</summary>
    private static async Task Shown(MainWindow window, string key, string path)
    {
        var owl = Owls(window, key).Single();
        var card = Tree(window).OfType<Border>().Last(b => b.Style == window.FindResource("Card") && Tree(b).Contains(owl));
        card.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible); window.UpdateLayout(); card.BringIntoView(); await Task.Delay(600); window.UpdateLayout();
        try
        {
            Check(owl.IsVisible && owl.ActualWidth >= 30 && Tree(card).OfType<TextBlock>().Any(t => t.IsVisible && t.Text.Length > 0),
                $"{key} sits in its card next to the text");
            window.SaveScreenshot(System.IO.Path.GetFullPath(path), 96);
        }
        finally { BindingOperations.GetBindingExpression(card, UIElement.VisibilityProperty)?.UpdateTarget(); window.UpdateLayout(); }
    }
}
