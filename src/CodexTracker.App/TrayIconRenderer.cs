using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace CodexTracker.App;

internal static class TrayIconRenderer
{
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

        // Rasterize hinted text at its final physical size. Resizing an enlarged
        // outline loses the font's pixel alignment and softens these tiny digits.
        using var glyph = FitGlyph(label, foreground, pixelSize, out var bounds);
        var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            int x = (pixelSize - bounds.Width) / 2;
            int y = ((known ? barY - 2 : pixelSize) - bounds.Height) / 2;
            graphics.DrawImageUnscaled(glyph, x - bounds.X, y - bounds.Y);
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

    private static Bitmap FitGlyph(string label, Color color, int size, out Rectangle bounds)
    {
        int maxHeight = (int)Math.Round(size * .65);
        float fontSize = size * .9f;
        for (;;)
        {
            var glyph = new Bitmap(size * 3, size * 2, PixelFormat.Format32bppArgb);
            try
            {
                using var graphics = Graphics.FromImage(glyph);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var font = new Font("Segoe UI Semibold", fontSize, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel);
                using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
                format.FormatFlags |= StringFormatFlags.NoClip;
                using var brush = new SolidBrush(color);
                graphics.DrawString(label, font, brush, 0, 0, format);
                bounds = InkBounds(glyph);
                if (bounds.Width <= size - 2 && bounds.Height <= maxHeight) return glyph;
                fontSize = Math.Min(fontSize - .5f, fontSize * Math.Min((size - 2f) / bounds.Width, (float)maxHeight / bounds.Height));
            }
            catch { glyph.Dispose(); throw; }
            glyph.Dispose();
        }
    }

    private static Rectangle InkBounds(Bitmap glyph)
    {
        var data = glyph.LockBits(new(0, 0, glyph.Width, glyph.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * glyph.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            int left = glyph.Width, top = glyph.Height, right = -1, bottom = -1;
            for (int y = 0; y < glyph.Height; y++)
            for (int x = 0; x < glyph.Width; x++)
                if (pixels[y * data.Stride + x * 4 + 3] != 0)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        finally { glyph.UnlockBits(data); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
