using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexTracker.Core;
using Color = System.Drawing.Color;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

namespace CodexTracker.App;

internal static class TrayIconRenderer
{
    /// <summary>Official Claude orange, kept for its spark as for the provider icons.</summary>
    internal static readonly Color ClaudeOrange = Color.FromArgb(217, 119, 87);

    internal static Icon Render(string number, Color color, bool dark, int pixelSize, AccountProvider? provider = null)
    {
        using var bitmap = RenderBitmap(number, color, dark, pixelSize, provider);
        IntPtr handle = bitmap.GetHicon();
        try { using var unmanaged = Icon.FromHandle(handle); return (Icon)unmanaged.Clone(); }
        finally { DestroyIcon(handle); }
    }

    /// <param name="provider">Tool whose quota is drawn: its sign starts the gauge (› Codex, orange spark Claude Code).</param>
    internal static Bitmap RenderBitmap(string number, Color color, bool dark, int pixelSize, AccountProvider? provider = null)
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
        int inset = Math.Max(1, (int)Math.Round(pixelSize / 10d));
        int barX = inset, barWidth = pixelSize - 2 * inset;
        int x = (pixelSize - bounds.Width) / 2;
        int y = ((known ? barY - 2 : pixelSize) - bounds.Height) / 2;
        // The sign takes the bottom-left corner, the gauge is centred on it and follows it.
        // The digits keep their size and only rise when they would touch it (28 px and above 32 px).
        var mark = provider is { } tool ? ProviderMark(tool, pixelSize, barHeight) : null;
        int markTop = 0;
        if (mark is not null)
        {
            int markHeight = mark.GetLength(0), markWidth = mark.GetLength(1);
            markTop = pixelSize - markHeight;
            barY = markTop + (markHeight - barHeight) / 2;
            barX = inset + markWidth + Math.Max(1, (int)Math.Round(pixelSize / 16d));
            barWidth = pixelSize - inset - barX;
            y = Math.Min(y, markTop - 1 - bounds.Height);
        }
        var bitmap = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
        try
        {
            var data = bitmap.LockBits(new(0, 0, pixelSize, pixelSize), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[data.Stride * pixelSize];
                CopyGlyph(pixels, data.Stride, glyph, stride, bounds, x, y);
                if (mark is not null) CopyMark(pixels, data.Stride, mark, provider == AccountProvider.ClaudeCode ? ClaudeOrange : foreground, inset, markTop);
                Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            }
            finally { bitmap.UnlockBits(data); }
            using var graphics = Graphics.FromImage(bitmap);
            if (known)
            {
                using var track = new SolidBrush(dark ? Color.FromArgb(95, 95, 95) : Color.FromArgb(155, 155, 155));
                using var progress = new SolidBrush(color);
                graphics.FillRectangle(track, barX, barY, barWidth, barHeight);
                int filled = (int)Math.Round(barWidth * remaining / 100d);
                if (filled > 0) graphics.FillRectangle(progress, barX, barY, filled, barHeight);
            }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    /// <summary>Pixel-aligned alpha of the tool's sign, with the gauge's stroke: › for Codex, an eight-ray spark for Claude Code.</summary>
    internal static byte[,] ProviderMark(AccountProvider provider, int pixelSize, int stroke)
    {
        // Same parity as the stroke so that the gauge sits exactly on the sign's middle.
        int size = Math.Max(3, (int)Math.Round(pixelSize * .22));
        if ((size - stroke) % 2 != 0) size += pixelSize < 18 ? -1 : 1;
        if (provider != AccountProvider.ClaudeCode)
        {
            var chevron = new byte[size, (size - 1) / 2 + stroke];
            for (int row = 0; row < size; row++)
            for (int k = 0; k < stroke; k++) chevron[row, Math.Min(row, size - 1 - row) + k] = 255;
            return chevron;
        }
        var spark = new byte[size, size];
        int from = (size - stroke) / 2;
        for (int i = 0; i < size; i++)
        for (int k = 0; k < stroke; k++) spark[i, from + k] = spark[from + k, i] = 255;
        // Small sparks dim their diagonal tips: a full 3 × 3 square would read as a dot.
        byte tip = size switch { 3 => 90, 5 => 170, _ => 255 };
        for (int row = 0; row < size; row++)
        for (int column = 0; column < size; column++)
        {
            int diagonal = Math.Min(Math.Abs(column - row), Math.Abs(column - (size - 1 - row)));
            bool corner = (row == 0 || row == size - 1) && (column == 0 || column == size - 1);
            byte alpha = diagonal == 0 ? (corner ? tip : (byte)255) : stroke > 1 && diagonal == 1 ? (byte)128 : (byte)0;
            spark[row, column] = Math.Max(spark[row, column], alpha);
        }
        return spark;
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
            if (bounds.IsEmpty) throw new InvalidOperationException(Loc.T("Le texte de l’icône n’a pas pu être rendu."));
            if (bounds.Width <= size - 2 && bounds.Height <= maxHeight) return pixels;
            fontSize = Math.Min(fontSize - .25, fontSize * Math.Min((size - 2d) / bounds.Width, (double)maxHeight / bounds.Height));
        }
        throw new InvalidOperationException(Loc.T("Le texte de l’icône dépasse la taille disponible."));
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

    private static void CopyGlyph(byte[] pixels, int targetStride, byte[] glyph, int stride, Rectangle bounds, int left, int top)
    {
        for (int y = 0; y < bounds.Height; y++)
        for (int x = 0; x < bounds.Width; x++)
        {
            int source = (y + bounds.Top) * stride + (x + bounds.Left) * 4;
            int alpha = glyph[source + 3];
            if (alpha == 0) continue;
            int target = (y + top) * targetStride + (x + left) * 4;
            // WPF uses premultiplied BGRA; System.Drawing's ARGB needs straight alpha.
            for (int channel = 0; channel < 3; channel++)
                pixels[target + channel] = (byte)Math.Clamp((glyph[source + channel] * 255 + alpha / 2) / alpha, 0, 255);
            pixels[target + 3] = (byte)alpha;
        }
    }

    private static void CopyMark(byte[] pixels, int targetStride, byte[,] mark, Color color, int left, int top)
    {
        for (int y = 0; y < mark.GetLength(0); y++)
        for (int x = 0; x < mark.GetLength(1); x++)
        {
            if (mark[y, x] == 0) continue;
            int target = (y + top) * targetStride + (x + left) * 4;
            pixels[target] = color.B; pixels[target + 1] = color.G; pixels[target + 2] = color.R; pixels[target + 3] = mark[y, x];
        }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
