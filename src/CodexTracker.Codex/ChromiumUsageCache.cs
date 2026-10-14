using System.Buffers.Binary;
using System.Text;

namespace CodexTracker.Codex;

/// <summary>Read-only, bounded adapter for Chromium's version 3 blockfile cache and response-info version 3.</summary>
// Layouts: chromium/src/net/disk_cache/blockfile/{disk_format.h,disk_format_base.h,addr.h}
// and net/http/http_response_info.cc. Unknown layouts remain unavailable.
internal sealed class ChromiumUsageCache(string directory) : IDisposable
{
    private const int IndexHeaderBytes = 368, BlockHeaderBytes = 8192, MaximumEntries = 32768;
    private const int MaximumIndexBytes = IndexHeaderBytes + 4 * 131072, MaximumKeyBytes = 4096, MaximumMetadataBytes = 16 * 1024;
    private readonly Dictionary<string, FileStream> _files = new(StringComparer.OrdinalIgnoreCase);
    internal sealed record Response(byte[] Body, string Encoding, DateTimeOffset ObservedAt);

    internal static Response? Read(string directory, Guid organization, DateTimeOffset now, CancellationToken token) =>
        Read(directory, "claude.ai", "/api/organizations/" + organization.ToString("D") + "/usage", now, token);

    /// <summary>Newest complete response of one exact HTTPS endpoint, whatever its query string.</summary>
    internal static Response? Read(string directory, string host, string path, DateTimeOffset now, CancellationToken token)
    {
        using var cache = new ChromiumUsageCache(directory);
        var indexFile = cache.Open("index");
        if (indexFile.Length < IndexHeaderBytes || indexFile.Length > MaximumIndexBytes) return null;
        var index = cache.Bytes(indexFile, 0, (int)indexFile.Length);
        if (U32(index, 0) != 0xC103CAC3 || U32(index, 4) != 0x30000 || I32(index, 8) is < 0 or > MaximumEntries) return null;
        var tableLength = I32(index, 28) is 0 ? 65536 : I32(index, 28);
        if (tableLength is < 1 or > 131072 || index.Length != IndexHeaderBytes + tableLength * 4) return null;
        var seen = new HashSet<uint>();
        Response? latest = null;
        for (var offset = IndexHeaderBytes; offset < index.Length; offset += 4)
        {
            var address = U32(index, offset);
            while (address != 0 && seen.Add(address))
            {
                token.ThrowIfCancellationRequested();
                if (seen.Count > MaximumEntries) return null;
                uint next = 0;
                try
                {
                    var entry = cache.Block(address, 96);
                    next = U32(entry, 4);
                    if (((address >> 28) & 7) != 2 || I32(entry, 20) != 0 || U32(entry, 72) != 0) continue;
                    var length = I32(entry, 32);
                    if (length is < 1 or > MaximumKeyBytes) continue;
                    var key = cache.Key(entry, address, length);
                    if (!Matches(key, host, path) || !cache.Clean(entry, address)) continue;
                    var metadata = cache.Block(U32(entry, 56), I32(entry, 40), MaximumMetadataBytes);
                    var info = Metadata(metadata, now);
                    if (info is null || latest is not null && info.Value.At <= latest.ObservedAt) continue;
                    var body = cache.Block(U32(entry, 60), I32(entry, 44), ClaudeDesktopCacheUsageReader.MaximumBodyBytes);
                    // The entry can be rewritten while Desktop is open: don't combine two generations.
                    if (!cache.Block(address, 96).AsSpan().SequenceEqual(entry) || !cache.Clean(entry, address) ||
                        !cache.Key(entry, address, length).AsSpan().SequenceEqual(key) ||
                        !cache.Block(U32(entry, 56), metadata.Length, MaximumMetadataBytes).AsSpan().SequenceEqual(metadata)) continue;
                    latest = new(body, info.Value.Encoding, info.Value.At);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException) { }
                finally { address = next; }
            }
        }
        return latest;
    }

    private static bool Matches(byte[] key, string host, string path)
    {
        var text = Encoding.UTF8.GetString(key);
        var url = text[(text.LastIndexOf(' ') + 1)..];
        if (url.StartsWith("0/0/", StringComparison.Ordinal) || url.StartsWith("1/0/", StringComparison.Ordinal)) url = url[4..];
        return url.StartsWith("https://", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && uri.Port == 443 &&
            uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsolutePath == path;
    }

    private bool Clean(byte[] entry, uint address)
    {
        var ranks = Block(U32(entry, 8), 36);
        return U32(ranks, 24) == address && I32(ranks, 28) == 0;
    }

    private byte[] Key(byte[] entry, uint address, int length) => U32(entry, 36) is var longKey && longKey != 0
        ? Block(longKey, length) : Block(address, 96 + length)[96..];

    internal static (DateTimeOffset At, string Encoding)? Metadata(byte[] bytes, DateTimeOffset now)
    {
        if (bytes.Length < 28 || U32(bytes, 0) != bytes.Length - 4) return null;
        var flags = U32(bytes, 4);
        if ((flags & 255) != 3 || (flags & (1 << 12)) != 0) return null; // Unsupported or incomplete response.
        var extra = (flags & 0x80000000) != 0 ? U32(bytes, 8) : 0;
        var cursor = (flags & 0x80000000) != 0 ? 12 : 8;
        var requestedAt = ChromeTime(bytes, cursor); cursor += 8;
        var receivedAt = ChromeTime(bytes, cursor); cursor += 8;
        // A revalidation doesn't make the cached percentages a new observation.
        var observedAt = receivedAt;
        if ((extra & 4) != 0) { observedAt = ChromeTime(bytes, cursor); cursor += 8; }
        if (requestedAt > receivedAt || observedAt > receivedAt || receivedAt > now || observedAt < now.AddDays(-30)) return null;
        var headerLength = I32(bytes, cursor); cursor += 4;
        if (headerLength is < 1 or > MaximumMetadataBytes || cursor + headerLength > bytes.Length) return null;
        var headers = Encoding.ASCII.GetString(bytes, cursor, headerLength).Split('\0');
        if (!headers[0].StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && !headers[0].StartsWith("HTTP/2 200", StringComparison.Ordinal)) return null;
        string encoding = "";
        foreach (var header in headers.Skip(1))
            if (header.StartsWith("content-encoding:", StringComparison.OrdinalIgnoreCase)) encoding = header[17..].Trim().ToLowerInvariant();
        return (observedAt, encoding);
    }

    private static DateTimeOffset ChromeTime(byte[] bytes, int offset) =>
        DateTimeOffset.FromFileTime(checked(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8)) * 10));

    private FileStream Open(string name)
    {
        if (!_files.TryGetValue(name, out var stream))
        {
            var path = Path.Combine(directory, name);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            _files[name] = stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        return stream;
    }

    private byte[] Block(uint address, int count, int limit = MaximumKeyBytes + 96)
    {
        if ((address & 0x80000000) == 0 || count < 1 || count > limit) throw new InvalidDataException();
        var type = (address >> 28) & 7;
        if (type == 0) return Bytes(Open("f_" + (address & 0x0FFFFFFF).ToString("x6")), 0, count);
        var blockSize = type switch { 1 => 36, 2 => 256, 3 => 1024, 4 => 4096, _ => throw new InvalidDataException() };
        if (count > blockSize * (((address >> 24) & 3) + 1)) throw new InvalidDataException();
        var fileNumber = (int)((address >> 16) & 255);
        var file = Open("data_" + fileNumber);
        var header = Bytes(file, 0, 16);
        if (U32(header, 0) != 0xC104CAC3 || U32(header, 4) != 0x20000 ||
            BinaryPrimitives.ReadInt16LittleEndian(header.AsSpan(8, 2)) != fileNumber || I32(header, 12) != blockSize) throw new InvalidDataException();
        return Bytes(file, BlockHeaderBytes + (address & 65535) * blockSize, count);
    }

    private byte[] Bytes(FileStream stream, long offset, int count)
    {
        if (offset < 0 || offset + count > stream.Length) throw new InvalidDataException();
        var bytes = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    public void Dispose() { foreach (var file in _files.Values) file.Dispose(); }
}
