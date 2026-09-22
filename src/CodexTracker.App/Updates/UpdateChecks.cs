using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTracker.App.Updates;

public enum UpdateCheckSource { Network, ValidatedCache, Cached, Unavailable }

public sealed record UpdateCheckResult(UpdateRelease? Release, UpdateCheckSource Source,
    DateTimeOffset? VerifiedAt, DateTimeOffset? NextCheckAt, string Message)
{
    public bool IsVerifiedNow => Source is UpdateCheckSource.Network or UpdateCheckSource.ValidatedCache;
}

public sealed partial class UpdateService
{
    public static Uri ReleasesPage { get; } = new("https://github.com/Aleqsd/codex-tracker/releases");
    private static readonly Uri FirstPage = new($"https://api.github.com/repos{Repository}/releases?per_page=100&page=1");
    private const int MaximumCacheBytes = 64 * 1024;
    private readonly object _checkGate = new();
    private readonly CancellationTokenSource _checksLifetime = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _cachePath;
    private string? CheckCachePath(bool previews) => _cachePath is null ? null : previews
        ? Path.Combine(Path.GetDirectoryName(_cachePath)!, Path.GetFileNameWithoutExtension(_cachePath) + "-preview.json") : _cachePath;
    private CheckCache _cache;
    private Task<UpdateCheckResult>? _checkInFlight;
    private bool _disposed;

    private sealed record CachedPage(Uri Uri, string? ETag, Uri? NextUri, UpdateRelease? Latest);
    private sealed record CheckCache(int Schema, DateTimeOffset? VerifiedAt, DateTimeOffset? NextCheckAt,
        int Failures, bool RateLimited, List<CachedPage> Pages, DateTimeOffset? RecordedAt, bool IncludePrereleases);
    private CheckCache EmptyCache() => new(3, null, null, 0, false, [], null, IncludePrereleases);

    /// <summary>Reads the last known result without making a network request.</summary>
    public UpdateCheckResult? ReadCachedCheck()
    {
        lock (_checkGate)
            return _cache.VerifiedAt is null && _cache.NextCheckAt is null ? null : CachedResult(_cache);
    }

    /// <summary>Checks manually. Concurrent callers share one request; cancelling a caller only stops its wait.</summary>
    public Task<UpdateCheckResult> CheckDetailedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<UpdateCheckResult> pending;
        lock (_checkGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_checkInFlight is { IsCompleted: false }) pending = _checkInFlight;
            else if (_cache.NextCheckAt > _clock()) return Task.FromResult(CachedResult(_cache));
            else pending = _checkInFlight = CheckCoreAsync(_cache, _channelRevision);
        }
        return pending.WaitAsync(cancellationToken);
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CheckCache previous, int channelRevision)
    {
        // Leave the caller's lock before sending requests, including synchronously completing test handlers.
        await Task.Yield();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_checksLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        DateTimeOffset? retryAt = null;
        var rateLimited = false;
        try
        {
            var pages = new List<CachedPage>();
            Uri? uri = FirstPage;
            var usedCache = false;
            while (uri is not null)
            {
                if (pages.Count >= 5) throw new InvalidDataException("La liste des versions est trop longue pour être vérifiée entièrement.");
                var cached = previous.Pages.FirstOrDefault(p => p.Uri == uri);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("CodexTracker/" + CurrentVersion);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (cached?.ETag is { } etag) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    rateLimited = true;
                    retryAt = RetryAt(response, _clock(), previous.Failures);
                    throw new IOException("GitHub limite temporairement les vérifications.");
                }
                CachedPage page;
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (cached?.ETag is null) throw new InvalidDataException("GitHub a renvoyé un cache qui n’est pas disponible.");
                    // A 304 reuses the body, not headers explicitly replaced by this response.
                    var tag = response.Headers.ETag?.ToString();
                    page = cached with
                    {
                        NextUri = response.Headers.Contains("Link") ? ReadNextPage(response, pages.Count + 2) : cached.NextUri,
                        ETag = tag is { Length: <= 1024 } ? tag : cached.ETag
                    };
                    usedCache = true;
                }
                else
                {
                    if (!response.IsSuccessStatusCode) throw new IOException("La vérification sur GitHub est momentanément indisponible.");
                    using var document = JsonDocument.Parse(await ReadBoundedAsync(response, 4 * 1024 * 1024, timeout.Token).ConfigureAwait(false));
                    if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("La réponse de GitHub est invalide.");
                    var tag = response.Headers.ETag?.ToString();
                    if (tag?.Length > 1024) tag = null;
                    page = new(uri, tag, ReadNextPage(response, pages.Count + 2), SelectRelease(document.RootElement, "0.0.0-0", previous.IncludePrereleases));
                }
                pages.Add(page);
                uri = page.NextUri;
            }
            var now = _clock();
            var next = new CheckCache(3, now, now.AddMinutes(5), 0, false, pages, now, previous.IncludePrereleases);
            PublishCache(next, channelRevision);
            lock (_checkGate)
                return _channelRevision != channelRevision ? CachedResult(_cache)
                    : new(BestRelease(next), usedCache ? UpdateCheckSource.ValidatedCache : UpdateCheckSource.Network,
                        now, next.NextCheckAt, "Versions vérifiées auprès de GitHub.");
        }
        catch (OperationCanceledException) when (_checksLifetime.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            var failures = Math.Min(previous.Failures + 1, 10);
            var now = _clock();
            var next = previous with { RecordedAt = now, NextCheckAt = retryAt ?? now.AddMinutes(Math.Min(60, Math.Pow(2, failures - 1))), Failures = failures, RateLimited = rateLimited };
            PublishCache(next, channelRevision);
            lock (_checkGate) return CachedResult(_channelRevision == channelRevision ? next : _cache);
        }
    }

    private UpdateRelease? BestRelease(CheckCache cache) => cache.Pages.Select(p => p.Latest)
        .Where(r => r is not null && IsReleaseAllowed(r, cache.IncludePrereleases) && SemanticVersion.Parse(r.Version)!.CompareTo(SemanticVersion.Parse(CurrentVersion)) > 0)
        .OrderByDescending(r => SemanticVersion.Parse(r!.Version)).FirstOrDefault();

    private UpdateCheckResult CachedResult(CheckCache cache)
    {
        var message = cache.Failures == 0 ? "Dernier résultat conservé en cache."
            : cache.RateLimited ? "GitHub limite temporairement les vérifications."
            : "GitHub n’a pas pu être vérifié. Le dernier résultat reste disponible.";
        return new(BestRelease(cache), cache.VerifiedAt is null ? UpdateCheckSource.Unavailable : UpdateCheckSource.Cached,
            cache.VerifiedAt, cache.NextCheckAt, message);
    }

    private static DateTimeOffset RetryAt(HttpResponseMessage response, DateTimeOffset now, int failures)
    {
        // Respect the longest server deadline. Without one, GitHub asks for at least a minute with backoff.
        var retry = now.AddMinutes(Math.Min(60, Math.Pow(2, Math.Min(failures, 10))));
        if (response.Headers.RetryAfter?.Date is { } date && date > retry) retry = date;
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            var dateFromDelta = delta < DateTimeOffset.MaxValue - now ? now + delta : DateTimeOffset.MaxValue;
            if (dateFromDelta > retry) retry = dateFromDelta;
        }
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            try { var reset = DateTimeOffset.FromUnixTimeSeconds(seconds); if (reset > retry) retry = reset; }
            catch (ArgumentOutOfRangeException) { }
        }
        return retry;
    }

    private static Uri? ReadNextPage(HttpResponseMessage response, int nextPage)
    {
        if (!response.Headers.TryGetValues("Link", out var links)) return null;
        foreach (var part in links.SelectMany(value => value.Split(',')))
        {
            var fields = part.Trim().Split(';', StringSplitOptions.TrimEntries);
            if (!fields.Skip(1).Any(field => field == "rel=\"next\"")) continue;
            if (!Uri.TryCreate(fields[0].Trim('<', '>'), UriKind.Absolute, out var uri) || !IsApiPage(uri, nextPage))
                throw new InvalidDataException("La page suivante des versions GitHub n’est pas autorisée.");
            return uri;
        }
        return null;
    }

    private static bool IsApiPage(Uri uri, int page)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host != "api.github.com" || uri.AbsolutePath != $"/repos{Repository}/releases") return false;
        var parameters = uri.Query.TrimStart('?').Split('&');
        return parameters.Length == 2 && parameters.Contains("per_page=100") && parameters.Contains($"page={page}");
    }

    private CheckCache LoadCheckCache()
    {
        var cachePath = CheckCachePath(IncludePrereleases);
        if (cachePath is null) return EmptyCache();
        try
        {
            UpdatePackage.RejectReparsePoints(cachePath);
            if (!File.Exists(cachePath)) return EmptyCache();
            using var input = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumCacheBytes) return EmptyCache();
            var bytes = new byte[input.Length];
            input.ReadExactly(bytes);
            var cache = JsonSerializer.Deserialize<CheckCache>(bytes);
            if (cache is null || cache.Schema != 3 || cache.IncludePrereleases != IncludePrereleases || cache.Pages is null || cache.Pages.Count > 5 || cache.Pages.Any(p => p is null) || cache.Failures is < 0 or > 10 ||
                (cache.VerifiedAt is null) != (cache.Pages.Count == 0) || !HasPlausibleCacheTimes(cache, _clock())) return EmptyCache();
            for (var index = 0; index < cache.Pages.Count; index++)
            {
                var page = cache.Pages[index];
                if (page is null || page.Uri is null || !IsApiPage(page.Uri, index + 1) ||
                    page.ETag is { } tag && (tag.Length > 1024 || !EntityTagHeaderValue.TryParse(tag, out _)) ||
                    page.NextUri != (index + 1 < cache.Pages.Count ? cache.Pages[index + 1].Uri : null) ||
                    page.Latest is { } release && (!IsCachedReleaseValid(release) || !IsReleaseAllowed(release, cache.IncludePrereleases))) return EmptyCache();
            }
            return cache;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { return EmptyCache(); }
    }

    private static bool HasPlausibleCacheTimes(CheckCache cache, DateTimeOffset now)
    {
        if (cache.RecordedAt is not { } recorded || cache.NextCheckAt is not { } next ||
            recorded > now.AddMinutes(5) || cache.VerifiedAt > recorded || next <= recorded) return false;
        var delay = next - recorded;
        if (cache.Failures == 0)
            return !cache.RateLimited && cache.VerifiedAt == recorded && delay == TimeSpan.FromMinutes(5);
        // Do not let a damaged local file or a shifted clock lock out manual checks indefinitely.
        // Normal GitHub reset/Retry-After deadlines are preserved, including across application restarts.
        // Recording follows header parsing, so the remaining delay can be fractionally below one minute.
        return delay <= (cache.RateLimited ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));
    }

    private static bool IsCachedReleaseValid(UpdateRelease release)
    {
        if (release.Tag is null || release.Tag.Length > 256 || SemanticVersion.Parse(release.Tag) is not { } version || version.Text != release.Version ||
            release.Size is <= 0 or > UpdatePackage.MaximumArchiveBytes || release.NotesUrl is null || release.DownloadUrl is null || release.ChecksumUrl is null ||
            !release.DownloadUrl.IsAbsoluteUri || !release.ChecksumUrl.IsAbsoluteUri) return false;
        var name = $"CodexTracker-{release.Version}-win-x64.zip";
        return release.NotesUrl == new Uri($"https://github.com{Repository}/releases/tag/{Uri.EscapeDataString(release.Tag)}") &&
            IsReleaseUri(release.DownloadUrl, release.Tag, name) && IsReleaseUri(release.ChecksumUrl, release.Tag, name + ".sha256");
    }

    private void PublishCache(CheckCache cache, int channelRevision)
    {
        lock (_checkGate)
        {
            if (_channelRevision != channelRevision) return;
            _cache = cache;
        }
        var cachePath = CheckCachePath(cache.IncludePrereleases);
        if (cachePath is null) return;
        string? temporary = null;
        try
        {
            UpdatePackage.RejectReparsePoints(cachePath);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(cache);
            if (bytes.Length > MaximumCacheBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            File.Move(temporary, cachePath, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
