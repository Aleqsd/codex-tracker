using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CodexTracker.App;
using ThemeMode = CodexTracker.App.ThemeMode;

internal static class TypographyChecks
{
    internal static async Task Run(MainWindow window)
    {
        var preferences = window.Preferences;
        var original = preferences.Current.ThemeMode;
        double width = window.Width, height = window.Height;
        try
        {
            foreach (var theme in new[] { ThemeMode.Dark, ThemeMode.Light })
            foreach (bool compact in new[] { false, true })
            {
                preferences.Update(p => p with { ThemeMode = theme });
                window.Width = compact ? 660 : 760; window.Height = compact ? 500 : 700;
                foreach (var page in new[] { "Comptes", "Resets", "Semaine", "Général", "Rappels", "Canaux", "Historique", "Calendrier", "Assistants", "Application" })
                {
                    if (page == "Semaine") window.ShowResetWeek(); else window.OpenPage(page);
                    await Task.Delay(50); window.UpdateLayout();
                    AssertBundled(window);
                    if (page is "Comptes" or "Semaine" or "Général" or "Assistants")
                        Ui.SaveScreenshot(window, Path.GetFullPath($"artifacts/previews/roboto-{page}-{theme}-{(compact ? "compact" : "normal")}-120.png"), 120);
                    Console.WriteLine($"PASS Bundled Roboto in {page}, {theme}, compact={compact}");
                }
            }
        }
        finally
        {
            preferences.Update(p => p with { ThemeMode = original });
            window.Width = width; window.Height = height; window.OpenPage("Comptes");
            await Task.Delay(50);
        }
    }

    internal static void AssertBundled(DependencyObject root)
    {
        foreach (var element in Tree(root).OfType<FrameworkElement>().Where(e => e.IsVisible && e is TextBlock or Control))
        {
            var family = (FontFamily)element.GetValue(TextElement.FontFamilyProperty);
            var weight = (FontWeight)element.GetValue(TextElement.FontWeightProperty);
            var style = (FontStyle)element.GetValue(TextElement.FontStyleProperty);
            var typeface = new Typeface(family, style, weight, FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyph) ||
                !glyph.FontUri.AbsoluteUri.Contains("CodexTracker;component/Assets/Fonts/", StringComparison.OrdinalIgnoreCase) ||
                glyph.Weight != weight || typeface.IsBoldSimulated)
                throw new InvalidOperationException($"Font fallback in {element.GetType().Name} ({element.Name}): {family.Source}, {weight}");
        }
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
