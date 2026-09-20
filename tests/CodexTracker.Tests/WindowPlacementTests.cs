using CodexTracker.App;
using Xunit;

namespace CodexTracker.Tests;

public class WindowPlacementTests
{
    [Theory]
    [InlineData(96, 322, 263)]
    [InlineData(144, 483, 395)]
    [InlineData(192, 644, 526)]
    public void Popup_size_uses_destination_dpi(uint dpi, int width, int height)
        => Assert.Equal(new PixelSize(width, height), WindowPlacement.ToPixels(322, 263, WindowPlacement.Scale(dpi)));

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Popup_stays_on_negative_origin_monitor(uint dpi)
    {
        var work = new PixelRect(-2560, -1440, 2560, 1392);
        var placement = WindowPlacement.Peek(new(-20, -20), WindowPlacement.ToPixels(322, 263, WindowPlacement.Scale(dpi)), work, WindowPlacement.Scale(dpi));
        AssertInside(placement, work);
        Assert.True(placement.X < 0 && placement.Y < 0);
    }

    [Theory]
    [InlineData(48, 0, 1872, 1080, 20, 1060)]
    [InlineData(0, 48, 1920, 1032, 1900, 20)]
    [InlineData(0, 0, 1872, 1080, 1900, 1060)]
    [InlineData(0, 0, 1920, 1032, 1900, 1060)]
    public void Taskbar_on_each_edge_keeps_popup_in_work_area(int x, int y, int width, int height, int anchorX, int anchorY)
    {
        var work = new PixelRect(x, y, width, height);
        AssertInside(WindowPlacement.Peek(new(anchorX, anchorY), new(644, 526), work, 2), work);
    }

    [Fact]
    public void Removed_monitor_window_moves_inside_surviving_monitor()
    {
        var work = new PixelRect(0, 0, 1920, 1032);
        var fitted = WindowPlacement.Constrain(new(-2200, -1000, 760, 620), work, 12);
        Assert.Equal(new PixelRect(12, 12, 760, 620), fitted);
    }

    [Fact]
    public void Visible_window_preserves_user_position_and_size()
    {
        var window = new PixelRect(-1800, 200, 900, 650);
        Assert.Equal(window, WindowPlacement.Constrain(window, new(-1920, 0, 1920, 1032), 12));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Tall_dialog_fits_1080p_work_area_at_every_scale(uint dpi)
    {
        var scale = WindowPlacement.Scale(dpi); var size = WindowPlacement.ToPixels(690, 745, scale);
        var work = new PixelRect(0, 0, 1920, 1032);
        var fitted = WindowPlacement.Constrain(new(0, 0, size.Width, size.Height), work, (int)(12 * scale));
        AssertInside(fitted, work);
        Assert.True(fitted.Height <= 1032 - 24 * scale);
    }

    [Fact]
    public void Tiny_work_area_does_not_throw_or_place_content_outside()
    {
        var work = new PixelRect(-8, -8, 8, 8);
        AssertInside(WindowPlacement.Peek(new(0, 0), new(644, 526), work, 2), work);
    }

    [Fact]
    public void Unavailable_dpi_falls_back_to_96_and_unmeasured_size_is_bounded()
    {
        Assert.Equal(1, WindowPlacement.Scale(0));
        Assert.Equal(new PixelSize(1, 1), WindowPlacement.ToPixels(double.NaN, double.PositiveInfinity, 1));
    }

    private static void AssertInside(PixelRect placement, PixelRect work)
    {
        Assert.InRange(placement.X, work.X, work.Right - 1);
        Assert.InRange(placement.Y, work.Y, work.Bottom - 1);
        Assert.InRange(placement.Right, placement.X + 1, work.Right);
        Assert.InRange(placement.Bottom, placement.Y + 1, work.Bottom);
    }
}
