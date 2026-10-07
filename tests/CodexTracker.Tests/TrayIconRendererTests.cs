using System.Drawing;
using CodexTracker.App;
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
    public void EveryPercentageKeepsSizeVisibleDigitsAndAlphaThroughNativeIcon(int size)
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
            // A hinted horizontal dash can land exactly on whole pixels.
            if (label != "--") Assert.True(antialiased, $"No antialiasing: {label}, {size}px");
        }
    }

    [Fact]
    public void TwoDigitsUseTheAvailableHeightAt125Percent()
    {
        using var bitmap = TrayIconRenderer.RenderBitmap("85", Color.White, true, 20);
        var ink = (from y in Enumerable.Range(0, 16)
                   from x in Enumerable.Range(0, 20)
                   where bitmap.GetPixel(x, y).A >= 64
                   select new Point(x, y)).ToArray();
        Assert.True(ink.Max(p => p.Y) - ink.Min(p => p.Y) + 1 >= 12);
        Assert.InRange(ink.Min(p => p.X), 1, 3);
        Assert.InRange(ink.Max(p => p.X), 16, 18);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BarShowsZeroFullWarningAndUnknownWithoutInventingUsage(bool dark)
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
    }
}
