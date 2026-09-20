namespace CodexTracker.App;

// All coordinates here are physical screen pixels, including negative monitor origins.
// DIP sizes are converted once, using the destination window's current DPI.
internal readonly record struct PixelPoint(int X, int Y);
internal readonly record struct PixelSize(int Width, int Height);
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

internal static class WindowPlacement
{
    public static double Scale(uint dpi) => dpi == 0 ? 1 : dpi / 96.0;
    public static PixelSize ToPixels(double width, double height, double scale)
        => new(ToPixelLength(width, scale), ToPixelLength(height, scale));
    private static int ToPixelLength(double length, double scale)
        => (int)Math.Clamp(Math.Ceiling(double.IsFinite(length) && length > 0 && double.IsFinite(scale) && scale > 0 ? length * scale : 1), 1, int.MaxValue);

    public static PixelRect Constrain(PixelRect bounds, PixelRect workArea, int margin = 0)
    {
        int workWidth = Math.Max(1, workArea.Width), workHeight = Math.Max(1, workArea.Height);
        int gapX = Math.Clamp(margin, 0, (workWidth - 1) / 2), gapY = Math.Clamp(margin, 0, (workHeight - 1) / 2);
        int width = Math.Clamp(bounds.Width, 1, workWidth - 2 * gapX), height = Math.Clamp(bounds.Height, 1, workHeight - 2 * gapY);
        return new(Math.Clamp(bounds.X, workArea.X + gapX, workArea.X + workWidth - gapX - width),
            Math.Clamp(bounds.Y, workArea.Y + gapY, workArea.Y + workHeight - gapY - height), width, height);
    }

    public static PixelRect Peek(PixelPoint anchor, PixelSize size, PixelRect workArea, double scale)
    {
        int gap = ToPixelLength(10, scale);
        int top = anchor.Y < workArea.Y ? workArea.Y + gap : anchor.Y - size.Height - gap;
        return Constrain(new(anchor.X - size.Width / 2, top, size.Width, size.Height), workArea, gap);
    }
}
