using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CodexTracker.Widgets;

/// <summary>Quota ring as a PNG data URI: a neutral track readable on light and dark boards, the arc in the state colour.</summary>
internal static class Ring
{
    internal static readonly Color Accent = Color.FromArgb(0x8E, 0x96, 0xFF), Warning = Color.FromArgb(0xE0, 0xA4, 0x58), Attention = Color.FromArgb(0xCF, 0x8E, 0x8E);
    private static readonly Dictionary<(int?, int), string> Cache = new();

    internal static string DataUri(int? percent, Color color)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((percent, color.ToArgb()), out var cached)) return cached;
            const int size = 184, stroke = 20;
            using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                var bounds = new RectangleF(stroke / 2f + 2, stroke / 2f + 2, size - stroke - 4, size - stroke - 4);
                using var track = new Pen(Color.FromArgb(70, 128, 128, 140), stroke);
                graphics.DrawEllipse(track, bounds);
                if (percent is > 0)
                {
                    using var arc = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    graphics.DrawArc(arc, bounds, -90, 360f * percent.Value / 100f);
                }
            }
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            var uri = "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
            if (Cache.Count > 64) Cache.Clear();
            return Cache[(percent, color.ToArgb())] = uri;
        }
    }
}
