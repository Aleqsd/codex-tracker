using System.Drawing;
using System.Runtime.ExceptionServices;
using CodexTracker.App;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class TrayIconRendererTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(20)] // 125% Windows scaling
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    public void EveryPercentageKeepsSizeVisibleDigitsAndAlphaThroughNativeIcon(int size) => OnSta(() =>
    {
        foreach (bool dark in new[] { true, false })
        foreach (string label in Enumerable.Range(0, 101).Select(n => n.ToString()).Append("--"))
        {
            using var icon = TrayIconRenderer.Render(label, Color.White, dark, size);
            using var bitmap = icon.ToBitmap();
            Assert.Equal(new Size(size, size), icon.Size);
            Assert.Equal(0, bitmap.GetPixel(0, 0).A);
            Assert.Equal(0, bitmap.GetPixel(size - 1, size - 1).A);
            bool visible = false, antialiased = false;
            for (int y = 0; y < size - Math.Max(4, size / 5); y++)
            for (int x = 0; x < size; x++)
            {
                byte alpha = bitmap.GetPixel(x, y).A;
                visible |= alpha >= 128;
                antialiased |= alpha is > 0 and < 255;
            }
            Assert.True(visible, $"No visible digits: {label}, {size}px");
            if (label != "--") Assert.True(antialiased, $"No grayscale alpha in native icon: {label}, {size}px");
        }
    });

    [Fact]
    public void TwoDigitsUseTheAvailableHeightAt125Percent() => OnSta(() =>
    {
        using var bitmap = TrayIconRenderer.RenderBitmap("85", Color.White, true, 20);
        var ink = (from y in Enumerable.Range(0, 16)
                   from x in Enumerable.Range(0, 20)
                   where bitmap.GetPixel(x, y).A >= 64
                   select new Point(x, y)).ToArray();
        Assert.True(ink.Max(p => p.Y) - ink.Min(p => p.Y) + 1 >= 12);
        Assert.InRange(ink.Min(p => p.X), 1, 3);
        Assert.InRange(ink.Max(p => p.X), 16, 18);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BarShowsZeroFullWarningAndUnknownWithoutInventingUsage(bool dark) => OnSta(() =>
    {
        var warning = Color.FromArgb(199, 170, 117);
        using var zero = TrayIconRenderer.RenderBitmap("0", warning, dark, 20);
        using var full = TrayIconRenderer.RenderBitmap("100", warning, dark, 20);
        using var low = TrayIconRenderer.RenderBitmap("20", warning, dark, 20);
        using var unknown = TrayIconRenderer.RenderBitmap("--", Color.Gray, dark, 20);
        var track = dark ? Color.FromArgb(95, 95, 95) : Color.FromArgb(155, 155, 155);
        Assert.Equal(track.ToArgb(), zero.GetPixel(2, 18).ToArgb());
        Assert.Equal(warning.ToArgb(), full.GetPixel(17, 18).ToArgb());
        Assert.Equal(warning.ToArgb(), low.GetPixel(2, 18).ToArgb());
        Assert.Equal(track.ToArgb(), low.GetPixel(17, 18).ToArgb());
        for (int x = 0; x < 20; x++) Assert.Equal(0, unknown.GetPixel(x, 18).A);
    });

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(28)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    public void ToolSignStartsTheGaugeWithoutTouchingOrShrinkingTheDigits(int size) => OnSta(() =>
    {
        int stroke = Math.Max(1, (int)Math.Round(size / 20d)), inset = Math.Max(1, (int)Math.Round(size / 10d));
        var state = Color.FromArgb(199, 170, 117);
        foreach (bool dark in new[] { true, false })
        foreach (var provider in new[] { AccountProvider.Codex, AccountProvider.ClaudeCode })
        foreach (string label in new[] { "0", "7", "85", "100", "--" })
        {
            using var plain = TrayIconRenderer.RenderBitmap(label, state, dark, size);
            using var marked = TrayIconRenderer.RenderBitmap(label, state, dark, size, provider);
            var mark = TrayIconRenderer.ProviderMark(provider, size, stroke);
            int height = mark.GetLength(0), width = mark.GetLength(1);
            int bottom = Enumerable.Range(0, size).Last(y => marked.GetPixel(inset, y).A > 0);
            int top = bottom - height + 1;
            var where = $"{provider}, {label}, {size}px, dark {dark}";
            // Every pixel of the sign is drawn where expected, in its colour: Claude orange, or the digits' ink for Codex.
            var ink = provider == AccountProvider.ClaudeCode ? TrayIconRenderer.ClaudeOrange
                : label == "--" ? state : dark ? Color.FromArgb(240, 240, 236) : Color.FromArgb(35, 35, 35);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var pixel = marked.GetPixel(inset + x, top + y);
                Assert.True(pixel.A == mark[y, x], $"Sign alpha at {x},{y}: {where}");
                if (mark[y, x] > 0) Assert.True(pixel.R == ink.R && pixel.G == ink.G && pixel.B == ink.B, $"Sign colour at {x},{y}: {where}");
            }
            Assert.True(top >= size / 2 && bottom < size, $"Sign in the lower half: {where}");
            Assert.True(Enumerable.Range(0, size).All(x => marked.GetPixel(x, top - 1).A == 0), $"An empty row separates digits and sign: {where}");
            // Common shell sizes keep exactly the former digits; others may only rise by a pixel.
            int digits = Enumerable.Range(0, top - 1).Count(y => Enumerable.Range(0, size).Any(x => marked.GetPixel(x, y).A > 0));
            int former = Enumerable.Range(0, top - 1).Count(y => Enumerable.Range(0, size).Any(x => plain.GetPixel(x, y).A > 0));
            Assert.True(digits >= former && digits > 0, $"Digits keep their height: {where}");
            if (size is 16 or 20 or 24 or 32)
                for (int y = 0; y < top - 1; y++)
                for (int x = 0; x < size; x++) Assert.True(plain.GetPixel(x, y) == marked.GetPixel(x, y), $"Digits unchanged at {x},{y}: {where}");
            // The gauge follows the sign after a gap, centred on it, and still ends at the former right edge.
            int barY = top + (height - stroke) / 2, barX = inset + width + Math.Max(1, (int)Math.Round(size / 16d));
            var track = dark ? Color.FromArgb(95, 95, 95) : Color.FromArgb(155, 155, 155);
            if (label == "--")
                Assert.True(Enumerable.Range(barX, size - barX).All(x => marked.GetPixel(x, barY).A == 0), $"No gauge for an unknown quota: {where}");
            else
            {
                Assert.Equal(0, marked.GetPixel(barX - 1, barY).A);
                Assert.Equal(label == "100" ? state.ToArgb() : track.ToArgb(), marked.GetPixel(size - inset - 1, barY).ToArgb());
                Assert.Equal(label == "0" ? track.ToArgb() : state.ToArgb(), marked.GetPixel(barX, barY + stroke - 1).ToArgb());
                Assert.Equal(0, marked.GetPixel(size - inset, barY).A);
            }
        }
    });

    [Fact]
    public void SignsAreDistinctAndSurviveTheNativeIcon() => OnSta(() =>
    {
        foreach (int size in new[] { 16, 20, 24, 32 })
        {
            using var claude = TrayIconRenderer.Render("63", Color.White, true, size, AccountProvider.ClaudeCode).ToBitmap();
            using var codex = TrayIconRenderer.Render("63", Color.White, true, size, AccountProvider.Codex).ToBitmap();
            var orange = TrayIconRenderer.ClaudeOrange.ToArgb();
            int Count(Bitmap bitmap) => (from y in Enumerable.Range(0, size) from x in Enumerable.Range(0, size) where bitmap.GetPixel(x, y).ToArgb() == orange select 0).Count();
            Assert.True(Count(claude) >= 5, $"Opaque orange spark in the native {size}px icon");
            Assert.Equal(0, Count(codex));
            var spark = TrayIconRenderer.ProviderMark(AccountProvider.ClaudeCode, size, Math.Max(1, (int)Math.Round(size / 20d)));
            var chevron = TrayIconRenderer.ProviderMark(AccountProvider.Codex, size, Math.Max(1, (int)Math.Round(size / 20d)));
            Assert.Equal(spark.GetLength(0), chevron.GetLength(0));
            Assert.True(chevron.GetLength(1) < spark.GetLength(1), "The chevron is narrower than the spark");
            // A small spark keeps its straight rays and dims its corners: never a filled square.
            Assert.Contains(spark.Cast<byte>(), alpha => alpha is > 0 and < 255);
        }
    });

    [Fact]
    public void RobotoWeightsComeFromEmbeddedResourcesInsteadOfInstalledFonts() => OnSta(() =>
    {
        foreach (var weight in new[] { System.Windows.FontWeights.Normal, System.Windows.FontWeights.Medium, System.Windows.FontWeights.Bold })
        {
            var typeface = new System.Windows.Media.Typeface(AppTypography.Family, System.Windows.FontStyles.Normal, weight, System.Windows.FontStretches.Normal);
            Assert.True(typeface.TryGetGlyphTypeface(out var glyph), $"Embedded Roboto missing: {weight}");
            Assert.Equal(weight.ToOpenTypeWeight(), glyph.Weight.ToOpenTypeWeight());
            Assert.Contains("Roboto", glyph.FamilyNames.Values);
            Assert.Contains("CodexTracker.Tests;component/Assets/Fonts/", glyph.FontUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
            Assert.False(typeface.IsBoldSimulated);
        }
        using var menuFont = new AppTypography.MenuFont();
        Assert.Equal("Roboto", menuFont.Font.FontFamily.Name);
    });

    private static void OnSta(Action action)
    {
        // Initialize WPF's pack URI scheme without creating an application or a window.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(System.Windows.Application).TypeHandle);
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
