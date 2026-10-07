using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

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

        // Render the bundled Roboto Medium directly at the shell's physical DPI.
        // Grayscale alpha survives HICON conversion without RGB fringes or resizing.
        var glyph = FitGlyph(label, foreground, pixelSize, out int stride, out var bounds);
        var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        try
        {
            int x = (pixelSize - bounds.Width) / 2;
            int y = ((known ? barY - 2 : pixelSize) - bounds.Height) / 2;
            CopyGlyph(bitmap, glyph, stride, bounds, x, y);
            using var graphics = Graphics.FromImage(bitmap);
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

    private static byte[] FitGlyph(string label, Color color, int size, out int stride, out Rectangle bounds)
    {
        int maxHeight = (int)Math.Round(size * .65);
        double pixelsPerDip = size / 16d;
        double fontSize = size * .9 / pixelsPerDip;
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var visual = new DrawingVisual();
            TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            TextOptions.SetTextHintingMode(visual, TextHintingMode.Fixed);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                AppTypography.TrayTypeface, fontSize, brush, pixelsPerDip);
            using (var drawing = visual.RenderOpen()) drawing.DrawText(text, new Point(0, 0));
            var rendered = new RenderTargetBitmap(size * 3, size * 2, 96 * pixelsPerDip, 96 * pixelsPerDip, PixelFormats.Pbgra32);
            rendered.Render(visual);
            stride = rendered.PixelWidth * 4;
            var pixels = new byte[stride * rendered.PixelHeight];
            rendered.CopyPixels(pixels, stride, 0);
            bounds = InkBounds(pixels, rendered.PixelWidth, rendered.PixelHeight);
            if (bounds.IsEmpty) throw new InvalidOperationException("Le texte de l’icône n’a pas pu être rendu.");
            if (bounds.Width <= size - 2 && bounds.Height <= maxHeight) return pixels;
            fontSize = Math.Min(fontSize - .25, fontSize * Math.Min((size - 2d) / bounds.Width, (double)maxHeight / bounds.Height));
        }
        throw new InvalidOperationException("Le texte de l’icône dépasse la taille disponible.");
    }

    private static Rectangle InkBounds(byte[] pixels, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (pixels[(y * width + x) * 4 + 3] != 0)
            { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static void CopyGlyph(Bitmap bitmap, byte[] glyph, int stride, Rectangle bounds, int left, int top)
    {
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * bitmap.Height];
            for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++)
            {
                int source = (y + bounds.Top) * stride + (x + bounds.Left) * 4;
                int alpha = glyph[source + 3];
                if (alpha == 0) continue;
                int target = (y + top) * data.Stride + (x + left) * 4;
                // WPF uses premultiplied BGRA; System.Drawing's ARGB needs straight alpha.
                for (int channel = 0; channel < 3; channel++)
                    pixels[target + channel] = (byte)Math.Clamp((glyph[source + channel] * 255 + alpha / 2) / alpha, 0, 255);
                pixels[target + 3] = (byte)alpha;
            }
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { bitmap.UnlockBits(data); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
