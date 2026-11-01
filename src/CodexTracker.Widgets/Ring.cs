using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CodexTracker.Widgets;

/// <summary>
/// Gauges drawn as PNG data URIs: the card format has no ring, and its container colours follow the board's theme
/// (light grey "accent"), so the quota colours would be lost. Neutral tracks stay readable on light and dark boards.
/// </summary>
internal static class Ring
{
    internal static readonly Color Accent = Color.FromArgb(0x8E, 0x96, 0xFF), Warning = Color.FromArgb(0xE0, 0xA4, 0x58), Attention = Color.FromArgb(0xCF, 0x8E, 0x8E);
    private static readonly Color Track = Color.FromArgb(70, 128, 128, 140);
    private static readonly Dictionary<string, string> Cache = new();

    /// <summary>Quota ring, 184 px square (shown at 64 px).</summary>
    internal static string DataUri(int? percent, Color color) => Cached($"ring/{percent}/{color.ToArgb()}", () =>
    {
        const int size = 184, stroke = 22;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Prepare(bitmap);
        var bounds = new RectangleF(stroke / 2f + 2, stroke / 2f + 2, size - stroke - 4, size - stroke - 4);
        using var track = new Pen(Track, stroke);
        graphics.DrawEllipse(track, bounds);
        if (percent is > 0)
        {
            using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawArc(arc, bounds, -90, 360f * percent.Value / 100f);
        }
        return bitmap;
    });

    /// <summary>Horizontal gauge, 600 × 14 px (stretched to the card width, so about 6 px high).</summary>
    internal static string Bar(int? percent, Color color) => Cached($"bar/{percent}/{color.ToArgb()}", () =>
    {
        const int width = 600, height = 14;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Prepare(bitmap);
        using (var track = new SolidBrush(Track)) Fill(graphics, track, new RectangleF(0, 0, width, height));
        if (percent is > 0)
            using (var fill = new SolidBrush(color)) Fill(graphics, fill, new RectangleF(0, 0, Math.Max(height, width * percent.Value / 100f), height));
        return bitmap;
    });

    /// <summary>Day cell of the week strip, 120 × 64 px: accent for a refill of an account in use, softer for another account.</summary>
    internal static string Cell(int level) => Cached($"cell/{level}", () =>
    {
        const int width = 120, height = 64;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Prepare(bitmap);
        using var brush = new SolidBrush(level switch { 2 => Accent, 1 => Color.FromArgb(150, Accent), _ => Track });
        Fill(graphics, brush, new RectangleF(4, 0, width - 8, height));
        return bitmap;
    });

    private static Graphics Prepare(Bitmap bitmap)
    {
        var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        return graphics;
    }

    private static void Fill(Graphics graphics, Brush brush, RectangleF bounds)
    {
        var radius = Math.Min(bounds.Height, bounds.Width) / 2f;
        if (bounds.Height > 20) radius = 14;
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    private static string Cached(string key, Func<Bitmap> draw)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            using var bitmap = draw();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            if (Cache.Count > 256) Cache.Clear();
            return Cache[key] = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
    }
}
