using System.Windows.Media.Imaging;

namespace CodexTracker.App;

internal static class AvatarStore
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new();
    private static string? Resolve(string directory, string? file) => file is not null &&
        file.EndsWith(".png", StringComparison.Ordinal) && Guid.TryParseExact(file[..^4], "N", out _)
            ? Path.Combine(directory, "avatars", file) : null;

    public static BitmapSource? Load(string directory, string? file)
    {
        var path = Resolve(directory, file); if (path is null) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        try { return Cache[path] = ReadImage(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.IO.FileFormatException)
        { return Cache[path] = null; }
    }
    public static BitmapSource ReadImage(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Choisissez une image enregistrée sur ce PC.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 8 * 1024 * 1024) throw new IOException("Choisissez une image de moins de 8 Mo.");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder is not PngBitmapDecoder and not JpegBitmapDecoder) throw new IOException("Choisissez un PNG ou JPEG.");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 16384 || frame.PixelHeight > 16384 ||
            (long)frame.PixelWidth * frame.PixelHeight > 40_000_000) throw new IOException("Image trop grande (40 mégapixels maximum).");
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 256; image.StreamSource = stream; image.EndInit(); image.Freeze();
        var side = Math.Min(image.PixelWidth, image.PixelHeight);
        if (side <= 0 || image.PixelHeight > 16384) throw new IOException("Dimensions de l’image non prises en charge.");
        var crop = new CroppedBitmap(image, new Int32Rect((image.PixelWidth - side) / 2, (image.PixelHeight - side) / 2, side, side));
        crop.Freeze(); return crop;
    }
    public static string Save(string directory, BitmapSource image)
    {
        var file = Guid.NewGuid().ToString("N") + ".png"; var path = Resolve(directory, file)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        try { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); encoder.Save(stream); }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        Cache[path] = image; return file;
    }
    public static void Remove(string directory, string? file)
    {
        var path = Resolve(directory, file); if (path is null) return;
        Cache.Remove(path);
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
