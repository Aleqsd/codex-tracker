using System.Text.Json;

namespace CodexTracker.Codex;

public sealed record LocalFileRead<T>(T? Value, bool RecoveryRequired, bool UsedBackup) where T : class;

// For display data and preferences only. Never roll back an outbox, a delivery journal or credentials.
public static class RecoverableJsonFile
{
    private const long MaximumBytes = 64_000_000;

    public static LocalFileRead<T> Read<T>(string path, Func<byte[], T> parse) where T : class
    {
        var present = File.Exists(path);
        if (present && TryRead(path, parse, out var value)) return new(value, false, false);
        var backup = path + ".bak";
        if (File.Exists(backup) && TryRead(backup, parse, out var recovered)) return new(recovered, true, true);
        return new(null, present || File.Exists(backup), false);
    }

    public static void Write<T>(string path, byte[] bytes, Func<byte[], T> parse) where T : class
    {
        // Validate before touching either generation. An invalid caller must not destroy a good backup.
        _ = parse(bytes) ?? throw new JsonException("Données locales absentes.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[]? previous = null;
        if (File.Exists(path))
        {
            // I/O failures abort the write; only successfully read but invalid JSON may be replaced.
            previous = ReadBytes(path);
            if (!IsValid(previous, parse))
            {
                AtomicWrite(path + ".corrupt-" + Guid.NewGuid().ToString("N"), previous);
                previous = null;
            }
        }
        var backup = path + ".bak";
        var backupExists = File.Exists(backup);
        var validBackup = backupExists && TryRead(backup, parse, out _);
        if (backupExists && !validBackup)
        {
            // Preserve a damaged backup as evidence before establishing the first valid generation.
            AtomicWrite(backup + ".corrupt-" + Guid.NewGuid().ToString("N"), ReadBytes(backup));
        }
        if (previous is not null) AtomicWrite(backup, previous);
        else if (!validBackup) AtomicWrite(backup, bytes);
        AtomicWrite(path, bytes);
    }

    private static bool TryRead<T>(string path, Func<byte[], T> parse, out T? value) where T : class
    {
        try { value = parse(ReadBytes(path)); return value is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { value = null; return false; }
    }

    private static bool IsValid<T>(byte[] bytes, Func<byte[], T> parse) where T : class
    {
        try { return parse(bytes) is not null; }
        catch (JsonException) { return false; }
    }

    private static byte[] ReadBytes(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Fichier local trop volumineux.");
        return File.ReadAllBytes(path);
    }

    private static void AtomicWrite(string destination, byte[] data)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(data); file.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
