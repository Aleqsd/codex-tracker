using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using Color = System.Drawing.Color;
using FontFamily = System.Drawing.FontFamily;
using Matrix = System.Drawing.Drawing2D.Matrix;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace CodexTracker.App;

internal static class TrayIconRenderer
{
    private const int Supersampling = 4;

    internal static Icon Render(string number, Color color, bool dark, int pixelSize)
    {
        using var bitmap = RenderBitmap(number, color, dark, pixelSize);
        IntPtr handle = bitmap.GetHicon();
        try { using var unmanaged = Icon.FromHandle(handle); return (Icon)unmanaged.Clone(); }
        finally { DestroyIcon(handle); }
    }

    internal static Bitmap RenderBitmap(string number, Color color, bool dark, int pixelSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelSize, 256);
        bool known = int.TryParse(number, out int remaining);
        remaining = Math.Clamp(remaining, 0, 100);
        string label = known ? remaining.ToString(CultureInfo.InvariantCulture) : "—";
        Color foreground = known ? (dark ? Color.FromArgb(240, 240, 236) : Color.FromArgb(35, 35, 35)) : color;
        int barHeight = Math.Max(1, (int)Math.Round(pixelSize / 20d));
        int barY = pixelSize - barHeight - Math.Max(1, (int)Math.Round(pixelSize / 20d));

        using var high = new Bitmap(pixelSize * Supersampling, pixelSize * Supersampling, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(high))
        {
            graphics.Clear(Color.Transparent);
            graphics.ScaleTransform(Supersampling, Supersampling);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var family = new FontFamily("Segoe UI Semibold");
            using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            using var path = new GraphicsPath();
            path.AddString(label, family, (int)System.Drawing.FontStyle.Regular, 64, PointF.Empty, format);
            var bounds = path.GetBounds();
            // Fit the visible glyphs, not the font's em box and invisible leading.
            // This is the approved B rendering: at 20 px the digits get ~13 px of height.
            float maxHeight = (float)Math.Round(pixelSize * .65);
            float scale = Math.Min((pixelSize - 2f) / bounds.Width, maxHeight / bounds.Height);
            float width = bounds.Width * scale, height = bounds.Height * scale;
            float x = (float)Math.Round((pixelSize - width) / 2);
            float y = (float)Math.Round(((known ? barY - 2 : pixelSize) - height) / 2);
            using var transform = new Matrix(scale, 0, 0, scale, x - bounds.X * scale, y - bounds.Y * scale);
            path.Transform(transform);
            using var ink = new SolidBrush(foreground);
            graphics.FillPath(ink, path);
        }

        var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            // Downsample ourselves into the final shell size; Explorer receives no
            // larger glyph bitmap to rescale. Premultiplied alpha avoids dark halos.
            graphics.DrawImage(high, new Rectangle(0, 0, pixelSize, pixelSize), 0, 0, high.Width, high.Height, GraphicsUnit.Pixel, attributes);
            graphics.CompositingMode = CompositingMode.SourceOver;
            if (known)
            {
                int inset = Math.Max(1, (int)Math.Round(pixelSize / 10d));
                int width = pixelSize - 2 * inset;
                using var track = new SolidBrush(dark ? Color.FromArgb(95, 95, 95) : Color.FromArgb(155, 155, 155));
                using var progress = new SolidBrush(color);
                graphics.FillRectangle(track, inset, barY, width, barHeight);
                int filled = (int)Math.Round(width * remaining / 100d);
                if (filled > 0) graphics.FillRectangle(progress, inset, barY, filled, barHeight);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
