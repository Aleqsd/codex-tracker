using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeDesktopCacheTests
{
    private static readonly Guid Organization = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid OtherOrganization = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly ClaudeCodeIdentity Identity = new("cache@example.test", ClaudeFixture.First + "/" + Organization, "max", null);
    private static string Usage(DateTimeOffset reset, double used = 42) => JsonSerializer.Serialize(new
    {
        five_hour = new { utilization = used, resets_at = reset.ToString("O") },
        seven_day = new { utilization = 60, resets_at = Now.AddDays(3).ToString("O") },
        extra_usage = new { irrelevant = "fixture-private-never-persist" }
    });

    [Theory]
    [InlineData("")] [InlineData("zstd")] [InlineData("br")] [InlineData("gzip")] [InlineData("deflate")]
    public void NativeCacheProvidesExactResetsWithoutCredentialsAndIsNeverModified(string encoding)
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-2), Usage(Now.AddHours(2)), encoding);
        var before = fixture.Files();
        var snapshot = Read(fixture)!;
        Assert.Equal(Now.AddMinutes(-2), snapshot.FetchedAt);
        Assert.Equal(Now.AddHours(2), snapshot.Short!.ResetsAt);
        Assert.Equal(58, snapshot.Short.RemainingPercent);
        Assert.Equal(Now.AddDays(3), snapshot.Weekly!.ResetsAt);
        Assert.False(snapshot.Short.IsResetEstimated);
        Assert.Null(snapshot.AvailableResetCredits);
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(snapshot));
        Assert.Equal(before.Keys, fixture.Files().Keys);
        foreach (var (name, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(name));
        Assert.False(File.Exists(fixture.Directory.File(".credentials.json")));
    }

    [Fact]
    public void OnlyTheSelectedOrganizationAndTheRealUsageEndpointAreDecoded()
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-10), Usage(Now.AddHours(1)));
        fixture.Add(OtherOrganization, Now.AddMinutes(-1), Usage(Now.AddHours(4)));
        fixture.Add(Organization, Now.AddMinutes(-1), Usage(Now.AddHours(3)), host: "example.test");
        fixture.Add(Organization, Now.AddMinutes(-1), Usage(Now.AddHours(3)), suffix: "/conversation");
        fixture.Add(Organization, Now.AddMinutes(-1), Usage(Now.AddHours(3)), host: "example.test/?url=https://claude.ai");
        Assert.Equal(Now.AddHours(1), Read(fixture)!.Short!.ResetsAt);
    }

    [Fact]
    public void TheNewestResponseWinsAndRevalidationKeepsTheOriginalObservationDate()
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-20), Usage(Now.AddHours(1)), responseAt: Now.AddMinutes(-1));
        fixture.Add(Organization, Now.AddMinutes(-5), Usage(Now.AddHours(2)), encoding: "zstd");
        var snapshot = Read(fixture)!;
        Assert.Equal(Now.AddMinutes(-5), snapshot.FetchedAt);
        Assert.Equal(Now.AddHours(2), snapshot.Short!.ResetsAt);
    }

    [Theory]
    [InlineData("{\"five_hour\":null,\"seven_day\":null}")]
    [InlineData("{}")]
    [InlineData("{\"five_hour\":{\"utilization\":null,\"resets_at\":null}}")]
    [InlineData("not-json")]
    [InlineData("{\"five_hour\":{\"utilization\":101}}")]
    [InlineData("{\"five_hour\":{\"utilization\":-1}}")]
    [InlineData("{\"five_hour\":{\"utilization\":\"50\"}}")]
    [InlineData("{\"five_hour\":{\"utilization\":50,\"resets_at\":\"2026-10-11\"}}")]
    [InlineData("{\"five_hour\":{\"utilization\":50,\"resets_at\":\"2026-10-11T11:00:00\"}}")]
    public void ANewerMissingOrInvalidQuotaCannotReanimateAnOlderCachedResponse(string latest)
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-10), Usage(Now.AddHours(1)));
        fixture.Add(Organization, Now.AddMinutes(-1), latest);
        Assert.Null(Read(fixture));
    }

    [Fact]
    public void IndependentWindowsAndMissingDatesStayIndependent()
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-1), "{\"five_hour\":{\"utilization\":0,\"resets_at\":null},\"seven_day\":null}");
        var snapshot = Read(fixture)!;
        Assert.Equal(100, snapshot.Short!.RemainingPercent);
        Assert.Null(snapshot.Short.ResetsAt);
        Assert.Null(snapshot.Weekly);
    }

    [Theory]
    [InlineData("dirty")] [InlineData("evicted")] [InlineData("future")] [InlineData("old")]
    [InlineData("truncated")] [InlineData("unsupported-response")] [InlineData("unsupported-block")]
    [InlineData("http-error")] [InlineData("bad-address")] [InlineData("oversize")]
    public void IncompleteStaleFutureAndUnsupportedEntriesAreIgnored(string invalid)
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, invalid == "future" ? Now.AddMinutes(1) : invalid == "old" ? Now.AddDays(-31) : Now.AddMinutes(-1),
            invalid == "oversize" ? new string('x', ClaudeDesktopCacheUsageReader.MaximumBodyBytes + 1) : Usage(Now.AddHours(1)), invalid: invalid);
        Assert.Null(Read(fixture));
    }

    [Fact]
    public void CyclesCancellationAndUnknownCacheVersionsAreBounded()
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-1), Usage(Now.AddHours(1)), invalid: "cycle");
        Assert.NotNull(Read(fixture));
        Assert.Throws<OperationCanceledException>(() => ClaudeDesktopCacheUsageReader.Read([fixture.HistoryPath], Identity, Now, new(true)));
        var index = File.ReadAllBytes(fixture.IndexPath); BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4), 0x40000);
        File.WriteAllBytes(fixture.IndexPath, index);
        Assert.Null(Read(fixture));
    }

    [Theory]
    [InlineData("zstd")] [InlineData("gzip")] [InlineData("br")]
    public void CompressedPayloadsCannotExceedTheDecodingLimit(string encoding)
    {
        var oversized = CacheFixture.Compress(Encoding.UTF8.GetBytes(new string('x', ClaudeDesktopCacheUsageReader.MaximumBodyBytes + 1)), encoding);
        Assert.ThrowsAny<Exception>(() => ClaudeDesktopCacheUsageReader.Decode(oversized, encoding));
    }

    [Theory]
    [InlineData("zstd")] [InlineData("gzip")] [InlineData("br")]
    public void DamagedCompressedResponsesRemainUnavailable(string encoding)
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-1), Usage(Now.AddHours(1)), encoding, invalid: "damaged-body");
        Assert.Null(Read(fixture));
    }

    [Fact]
    public async Task TrackerReadsDesktopAutomaticallyAndKeepsOrganizationsSeparateWithoutTerminalCredentials()
    {
        using var fixture = new CacheFixture();
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);
        var reset = at.AddHours(2);
        ClaudeFixture.SignIn(fixture.Directory);
        File.Delete(fixture.Directory.File(".credentials.json"));
        fixture.Add(Organization, at, Usage(reset), "zstd");
        var options = new TrackerServiceOptions { DataDirectory = fixture.Directory.File("tracker"),
            ClaudeLocation = ClaudeFixture.Location(fixture.Directory), ClaudeDesktopUsagePaths = [fixture.HistoryPath],
            AutomaticRefresh = false, MonitorAuthChanges = false };
        await using var service = new TrackerService(fixture.Directory.File("missing-auth.json"), options);
        await service.InitializeAsync();
        var personal = service.State.ActiveAccount!;
        Assert.Equal(reset, personal.Snapshot!.Short!.ResetsAt);
        Assert.False(personal.Snapshot.Short.IsResetEstimated);
        Assert.Equal(at.ToUnixTimeMilliseconds(), personal.Snapshot.FetchedAt.ToUnixTimeMilliseconds());

        var later = at.AddMinutes(5);
        File.WriteAllText(fixture.HistoryPath, JsonSerializer.Serialize(new { version = 2,
            samples = new[] { new { t = later.ToUnixTimeMilliseconds(), org = Organization, u = new { fh = 55, sd = 65 } } } }));
        await service.RefreshAsync();
        Assert.Equal(45, service.State.ActiveAccount!.Snapshot!.Short!.RemainingPercent);
        Assert.Equal(reset, service.State.ActiveAccount.Snapshot.Short.ResetsAt);
        Assert.Equal(later.ToUnixTimeMilliseconds(), service.State.ActiveAccount.Snapshot.FetchedAt.ToUnixTimeMilliseconds());

        ClaudeFixture.SignIn(fixture.Directory, organization: OtherOrganization.ToString());
        File.Delete(fixture.Directory.File(".credentials.json"));
        await service.RefreshAsync();
        Assert.NotEqual(personal.Profile.Id, service.State.ActiveAccount!.Profile.Id);
        Assert.Null(service.State.ActiveAccount.Snapshot);
        Assert.Equal(reset, service.State.Accounts.Single(a => a.Profile.Id == personal.Profile.Id).Snapshot!.Short!.ResetsAt);
        fixture.Add(OtherOrganization, later.AddMinutes(1), Usage(reset.AddHours(1), 20), "br");
        await service.RefreshAsync();
        Assert.Equal(80, service.State.ActiveAccount!.Snapshot!.Short!.RemainingPercent);
        Assert.Equal(reset.AddHours(1), service.State.ActiveAccount.Snapshot.Short.ResetsAt);
        Assert.DoesNotContain("fixture-private", File.ReadAllText(Path.Combine(options.DataDirectory, "snapshots.json")));
    }

    [Fact]
    public void FresherHistoryKeepsItsPercentagesAndReceivesOnlyStillValidExactDates()
    {
        using var fixture = new CacheFixture();
        fixture.Add(Organization, Now.AddMinutes(-20), Usage(Now.AddHours(1)), encoding: "zstd");
        File.WriteAllText(fixture.HistoryPath, JsonSerializer.Serialize(new { version = 2,
            samples = new[] { new { t = Now.AddMinutes(-5).ToUnixTimeMilliseconds(), org = Organization, u = new { fh = 55, sd = 65 } } } }));
        // Production reads against UtcNow; use a deterministic cache observation for this merge check.
        var cached = Read(fixture)!;
        using var history = JsonDocument.Parse(File.ReadAllText(fixture.HistoryPath));
        var latest = ClaudeDesktopUsageReader.Parse(history.RootElement, Identity, Organization, Now)!;
        var merged = ClaudeCodeUsageReader.KeepTerminalResets(latest, cached);
        Assert.Equal(45, merged.Short!.RemainingPercent); Assert.Equal(35, merged.Weekly!.RemainingPercent);
        Assert.Equal(latest.FetchedAt, merged.FetchedAt); Assert.Equal(Now.AddHours(1), merged.Short.ResetsAt);
        var after = merged with { FetchedAt = Now.AddHours(2), Buckets = latest.Buckets };
        Assert.Null(ClaudeCodeUsageReader.KeepTerminalResets(after, cached).Short!.ResetsAt);
    }

    [Fact]
    public async Task AnExactShortDateSurvivesCacheEvictionAndRestartButIsNeverProjected()
    {
        using var fixture = new CacheFixture();
        var at = DateTimeOffset.UtcNow.AddMinutes(-10); var reset = at.AddHours(2);
        ClaudeFixture.SignIn(fixture.Directory); File.Delete(fixture.Directory.File(".credentials.json"));
        fixture.Add(Organization, at, Usage(reset), "zstd");
        var options = new TrackerServiceOptions { DataDirectory = fixture.Directory.File("tracker"),
            ClaudeLocation = ClaudeFixture.Location(fixture.Directory), ClaudeDesktopUsagePaths = [fixture.HistoryPath],
            AutomaticRefresh = false, MonitorAuthChanges = false };
        Guid id;
        await using (var service = new TrackerService(fixture.Directory.File("missing-auth.json"), options))
        {
            await service.InitializeAsync(); id = service.State.ActiveAccount!.Profile.Id;
            fixture.Evict();
            File.WriteAllText(fixture.HistoryPath, JsonSerializer.Serialize(new { version = 2,
                samples = new[] { new { t = at.AddMinutes(5).ToUnixTimeMilliseconds(), org = Organization, u = new { fh = 55, sd = 65 } } } }));
            await service.RefreshAsync();
            Assert.Equal(reset, service.State.ActiveAccount!.Snapshot!.Short!.ResetsAt);
        }
        await using var restarted = new TrackerService(fixture.Directory.File("missing-auth.json"), options);
        await restarted.InitializeAsync();
        Assert.Equal(reset, restarted.State.ActiveAccount!.Snapshot!.Short!.ResetsAt);
        Assert.Null(ClaudeCodeUsageReader.PendingExactShortReset(restarted.GetHistory(id), reset));
        Assert.Null(ClaudeCodeUsageReader.PendingExactShortReset(restarted.GetHistory(id), reset.AddDays(1)));
        Assert.Null(ClaudeCodeUsageReader.PendingExactShortReset(restarted.GetHistory(id), at.AddSeconds(-1)));
    }

    private static AccountSnapshot? Read(CacheFixture fixture) => ClaudeDesktopCacheUsageReader.Read([fixture.HistoryPath], Identity, Now, default);

    internal sealed class CacheFixture : IDisposable
    {
        internal TestDirectory Directory { get; } = new();
        internal string HistoryPath => Directory.File("plan-usage-history.json");
        internal string IndexPath => Path.Combine(_cache, "index");
        private readonly string _cache;
        private readonly byte[] _index = new byte[368 + 65536 * 4];
        private readonly Dictionary<int, MemoryStream> _blocks = new();
        private int _count;

        internal CacheFixture()
        {
            _cache = System.IO.Directory.CreateDirectory(Directory.File("Cache/Cache_Data")).FullName;
            Write(_index, 0, 0xC103CAC3); Write(_index, 4, 0x30000);
        }

        internal void Add(Guid organization, DateTimeOffset observedAt, string json, string encoding = "", string host = "claude.ai",
            string suffix = "", DateTimeOffset? responseAt = null, string? invalid = null, string? url = null)
        {
            url ??= $"https://{host}/api/organizations/{organization:D}/usage{suffix}";
            var key = Encoding.UTF8.GetBytes("1/0/_dk_https://claude.ai https://claude.ai " + url);
            var body = Compress(Encoding.UTF8.GetBytes(json), encoding);
            if (invalid == "damaged-body") body[0] ^= 255;
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {(invalid == "http-error" ? "403" : "200")}\0content-type: application/json\0content-encoding:{encoding}\0\0");
            var metadata = new byte[40 + ((headers.Length + 3) / 4) * 4];
            Write(metadata, 0, (uint)(metadata.Length - 4)); Write(metadata, 4, invalid == "unsupported-response" ? 4 : 0x80000003u | (invalid == "truncated" ? 1u << 12 : 0));
            Write(metadata, 8, 4);
            var received = responseAt ?? observedAt;
            BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(12), received.AddSeconds(-1).ToFileTime() / 10);
            BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(20), received.ToFileTime() / 10);
            BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(28), observedAt.ToFileTime() / 10);
            Write(metadata, 36, (uint)headers.Length); headers.CopyTo(metadata, 40);
            var metadataAddress = Allocate(2, 3, 1024, metadata);
            var bodyAddress = Allocate(3, 4, 4096, body);
            var entry = new byte[96 + key.Length];
            Write(entry, 4, BinaryPrimitives.ReadUInt32LittleEndian(_index.AsSpan(368)));
            Write(entry, 20, invalid == "evicted" ? 1u : 0);
            Write(entry, 32, (uint)key.Length); Write(entry, 40, (uint)metadata.Length); Write(entry, 44, (uint)body.Length);
            Write(entry, 56, metadataAddress); Write(entry, 60, invalid == "bad-address" ? 0 : bodyAddress);
            key.CopyTo(entry, 96);
            var entryAddress = Allocate(1, 2, 256, entry);
            var ranks = new byte[36]; Write(ranks, 24, entryAddress); Write(ranks, 28, invalid == "dirty" ? 1u : 0);
            var rankAddress = Allocate(0, 1, 36, ranks);
            Write(entry, 8, rankAddress);
            if (invalid == "cycle") Write(entry, 4, entryAddress);
            _blocks[1].Position = 8192 + (entryAddress & 65535) * 256; _blocks[1].Write(entry);
            Write(_index, 368, entryAddress); Write(_index, 8, (uint)++_count);
            File.WriteAllBytes(IndexPath, _index);
            foreach (var (number, stream) in _blocks)
            {
                var bytes = stream.ToArray();
                if (invalid == "unsupported-block" && number == 1) Write(bytes, 4, 0x30000);
                File.WriteAllBytes(Path.Combine(_cache, "data_" + number), bytes);
            }
        }

        private uint Allocate(int fileNumber, int kind, int blockSize, byte[] bytes)
        {
            if (bytes.Length > blockSize * 4)
            {
                var file = 1 + System.IO.Directory.GetFiles(_cache, "f_*").Length;
                File.WriteAllBytes(Path.Combine(_cache, "f_" + file.ToString("x6")), bytes);
                return 0x80000000u | (uint)file;
            }
            if (!_blocks.TryGetValue(fileNumber, out var stream))
            {
                stream = new(); stream.SetLength(8192);
                var header = new byte[16]; Write(header, 0, 0xC104CAC3); Write(header, 4, 0x20000);
                BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(8), (short)fileNumber); Write(header, 12, (uint)blockSize);
                stream.Write(header); _blocks[fileNumber] = stream;
            }
            var block = (int)((stream.Length - 8192) / blockSize);
            var count = (bytes.Length + blockSize - 1) / blockSize;
            stream.Position = stream.Length; stream.Write(bytes); stream.SetLength(8192 + (block + count) * blockSize);
            return 0x80000000u | (uint)kind << 28 | (uint)(count - 1) << 24 | (uint)fileNumber << 16 | (uint)block;
        }

        internal static byte[] Compress(byte[] body, string encoding)
        {
            if (encoding == "") return body;
            if (encoding == "zstd") { using var compressor = new ZstdSharp.Compressor(); return compressor.Wrap(body).ToArray(); }
            using var output = new MemoryStream();
            using (Stream compressed = encoding switch {
                "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
                _ => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true) }) compressed.Write(body);
            return output.ToArray();
        }

        internal Dictionary<string, byte[]> Files() => System.IO.Directory.GetFiles(_cache).Order().ToDictionary(p => p, File.ReadAllBytes);
        internal void Evict()
        {
            var empty = new byte[_index.Length]; _index.AsSpan(0, 368).CopyTo(empty); Write(empty, 8, 0);
            File.WriteAllBytes(IndexPath, empty);
        }
        private static void Write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        public void Dispose() { foreach (var stream in _blocks.Values) stream.Dispose(); Directory.Dispose(); }
    }
}
