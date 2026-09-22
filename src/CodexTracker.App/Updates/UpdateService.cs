using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTracker.App.Updates;

public sealed record UpdateRelease(string Version, string Tag, Uri NotesUrl, Uri DownloadUrl, Uri ChecksumUrl, long Size, bool IsPrerelease = false);
public sealed record UpdateOutcome(DateTimeOffset At, bool Success, string Message);

public sealed partial class UpdateService : IDisposable
{
    private const string Repository = "/Aleqsd/codex-tracker";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    public string CurrentVersion { get; }
    public bool IncludePrereleases { get; private set; }
    private int _channelRevision;
    public event EventHandler? ChannelChanged;

    public void SetIncludePrereleases(bool include)
    {
        lock (_checkGate)
        {
            if (IncludePrereleases == include) return;
            IncludePrereleases = include;
            _channelRevision++;
            _checkInFlight = null;
            _cache = LoadCheckCache();
            Prepared = null;
            PreparationMessage = null;
        }
        ChannelChanged?.Invoke(this, EventArgs.Empty);
        PreparationChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsReleaseAllowed(UpdateRelease release, bool includePrereleases) =>
        includePrereleases || !release.IsPrerelease && SemanticVersion.Parse(release.Version)?.PreRelease is null;
    internal static string UpdatesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker", "updates");

    public UpdateService() : this(typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0", null) { }
    internal UpdateService(string version, HttpClient? client, string? cachePath = null, Func<DateTimeOffset>? clock = null)
    {
        CurrentVersion = SemanticVersion.Parse(version)?.Text ?? "0.0.0";
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null
        }) { Timeout = TimeSpan.FromMinutes(5) };
        if (_client.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("Le service de mise à jour n’utilise aucun jeton d’authentification.");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _cachePath = cachePath ?? (_ownsClient ? Path.Combine(UpdatesRoot, "release-cache.json") : null);
        _cache = LoadCheckCache();
        if (_ownsClient) CleanupOldStages();
    }

    public UpdateOutcome? ReadLastResult()
    {
        try
        {
            var path = Path.Combine(UpdatesRoot, "last-result.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateOutcome>(File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var result = await CheckDetailedAsync(cancellationToken).ConfigureAwait(false);
        // The legacy API cannot express stale data. Never turn a failed check into "up to date".
        if (!result.IsVerifiedNow) throw new IOException(result.Message);
        return result.Release;
    }

    internal static UpdateRelease? SelectRelease(JsonElement releases, string currentVersion, bool includePrereleases = false)
    {
        var current = SemanticVersion.Parse(currentVersion) ?? throw new InvalidDataException("Version actuelle invalide.");
        UpdateRelease? best = null;
        foreach (var row in releases.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
                !row.TryGetProperty("tag_name", out var tagNode) || tagNode.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            var tag = tagNode.GetString()!;
            if (tag.Length > 256) continue;
            var version = SemanticVersion.Parse(tag);
            if (version is null || version.CompareTo(current) <= 0 || best is not null && version.CompareTo(SemanticVersion.Parse(best.Version)) <= 0) continue;
            var isPrerelease = version.PreRelease is not null || row.TryGetProperty("prerelease", out var preview) && preview.ValueKind == JsonValueKind.True;
            if (isPrerelease && !includePrereleases) continue;
            var name = $"CodexTracker-{version.Text}-win-x64.zip";
            Uri? download = null; Uri? checksum = null; long size = 0;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String ||
                    !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)) continue;
                var assetName = n.GetString();
                if (assetName != name && assetName != name + ".sha256") continue;
                if (!IsReleaseUri(uri, tag, assetName)) continue;
                if (assetName == name && asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var length) && length is > 0 and <= UpdatePackage.MaximumArchiveBytes)
                { download = uri; size = length; }
                if (assetName == name + ".sha256") checksum = uri;
            }
            if (download is not null && checksum is not null)
                best = new(version.Text, tag, new Uri($"https://github.com{Repository}/releases/tag/{Uri.EscapeDataString(tag)}"), download, checksum, size, isPrerelease);
        }
        return best;
    }

    public async Task StageAndLaunchAsync(UpdateRelease release, int processId, CancellationToken cancellationToken = default)
    {
        if (processId != Environment.ProcessId) throw new InvalidOperationException("Le processus de mise à jour ne correspond pas à cette application.");
        await PrepareAsync(release, cancellationToken);
        if (!await LaunchPreparedAsync(false, false, cancellationToken))
            throw new IOException("La mise à jour n’est plus prête. Recherchez-la à nouveau.");
    }

    public static bool CanSelfUpdate => Environment.ProcessPath is { } path &&
        string.Equals(Path.GetFileName(path), "CodexTracker.exe", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "CodexTracker.dll"));

    public async Task<bool> LaunchPreparedAsync(bool automatic, bool background, CancellationToken cancellationToken = default)
    {
        var channelRevision = _channelRevision;
        var target = Environment.ProcessPath ?? throw new IOException("L’application en cours est introuvable.");
        if (!string.Equals(Path.GetFileName(target), "CodexTracker.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Utilisez la version installée ou portable pour effectuer la mise à jour.");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(target)!, "CodexTracker.dll")))
            throw new IOException("La mise à jour automatique nécessite la version autonome installée ou portable.");
        UpdatePackage.RejectReparsePoints(target);
        UpdatePackage.RejectReparsePoints(UpdatesRoot);
        var prepared = await ClaimPreparedAsync(automatic, cancellationToken);
        if (prepared is null) return false;
        EnsureChannel(channelRevision);
        var stage = Path.Combine(UpdatesRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var helperStarted = false;
        try
        {
            var executable = Path.Combine(stage, "payload", "CodexTracker.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.Copy(PreparedExecutable(prepared), executable, false);
            if (!string.Equals(await UpdatePackage.HashAsync(executable, cancellationToken), prepared.ExecutableHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Le fichier téléchargé a changé depuis la préparation.");
            var helper = Path.Combine(stage, "CodexTracker.UpdateHelper.exe");
            File.Copy(target, helper, false);
            using var current = Process.GetCurrentProcess();
            var manifest = new UpdateManifest(target, executable, await UpdatePackage.HashAsync(target, cancellationToken),
                prepared.ExecutableHash, Environment.ProcessId, current.StartTime.ToUniversalTime().Ticks, background);
            var manifestPath = Path.Combine(stage, "update.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken);
            var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
            info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(manifestPath);
            Process child;
            lock (_checkGate)
            {
                EnsureChannel(channelRevision);
                child = Process.Start(info) ?? throw new IOException("Le programme de mise à jour n’a pas démarré.");
            }
            using var childLifetime = child;
            helperStarted = true;
            var ready = Path.Combine(stage, "helper.ready");
            await WaitForHelperReadyAsync(() => File.Exists(ready), () => child.HasExited,
                () => EnsureChannel(channelRevision),
                () => File.WriteAllTextAsync(Path.Combine(stage, "cancel"), "cancel", CancellationToken.None), cancellationToken);
            // The caller now closes this app normally. The helper never closes Codex or kills this app.
            return true;
        }
        catch
        {
            if (!helperStarted)
                try { await File.WriteAllTextAsync(Path.Combine(stage, "abandoned"), "abandoned", CancellationToken.None); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static async Task WaitForHelperReadyAsync(Func<bool> ready, Func<bool> exited, Action ensureCurrentChannel,
        Func<Task> cancel, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (!ready())
            {
                ensureCurrentChannel();
                if (exited()) throw new IOException("Le programme de mise à jour n’a pas pu préparer l’installation.");
                await Task.Delay(100, wait.Token);
            }
            // Readiness is not permission to install after the user changes release channels.
            ensureCurrentChannel();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch { await cancel(); throw; }
    }

    internal async Task<string> StagePackageAsync(UpdateRelease release, string stage, CancellationToken token)
    {
        var version = SemanticVersion.Parse(release.Tag);
        var name = $"CodexTracker-{release.Version}-win-x64.zip";
        if (version?.Text != release.Version || version.CompareTo(SemanticVersion.Parse(CurrentVersion)) <= 0 ||
            release.Size is <= 0 or > UpdatePackage.MaximumArchiveBytes || !IsReleaseUri(release.DownloadUrl, release.Tag, name) ||
            !IsReleaseUri(release.ChecksumUrl, release.Tag, name + ".sha256")) throw new InvalidDataException("La source de mise à jour n’est pas autorisée.");
        UpdatePackage.RejectReparsePoints(stage);
        Directory.CreateDirectory(stage);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var checksumResponse = await GetAsync(release.ChecksumUrl, true, timeout.Token);
        var checksum = Encoding.ASCII.GetString(await ReadBoundedAsync(checksumResponse, 4096, timeout.Token));
        var archive = Path.Combine(stage, "package.zip");
        using (var response = await GetAsync(release.DownloadUrl, true, timeout.Token))
        {
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("La taille de la mise à jour ne correspond pas à la publication.");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyBoundedAsync(source, output, release.Size, timeout.Token);
            if (output.Length != release.Size) throw new InvalidDataException("Le téléchargement de la mise à jour est incomplet.");
        }
        await UpdatePackage.VerifyAsync(archive, checksum, name, timeout.Token);
        return await UpdatePackage.ExtractExecutableAsync(archive, Path.Combine(stage, "payload"), timeout.Token);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, bool asset, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("CodexTracker/" + CurrentVersion);
            request.Headers.Accept.ParseAdd(asset ? "application/octet-stream" : "application/vnd.github+json");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var next = response.Headers.Location;
                response.Dispose();
                if (!asset || next is null || !next.IsAbsoluteUri || next.Scheme != "https" || next.UserInfo.Length != 0 || !next.IsDefaultPort ||
                    !(next.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || next.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("GitHub a renvoyé une redirection de téléchargement non autorisée.");
                uri = next; continue;
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            { response.Dispose(); throw new IOException("GitHub limite temporairement les vérifications. Réessayez plus tard."); }
            if (!response.IsSuccessStatusCode)
            { response.Dispose(); throw new IOException("La mise à jour est indisponible sur GitHub. Réessayez plus tard."); }
            return response;
        }
        throw new IOException("Le téléchargement comporte trop de redirections.");
    }

    private static bool IsReleaseUri(Uri uri, string tag, string asset) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.Equals($"{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("La réponse de mise à jour est trop volumineuse.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        await CopyBoundedAsync(source, memory, maximum, token);
        return memory.ToArray();
    }

    internal static async Task CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken token)
    {
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, token);
            if (count == 0) break;
            total = checked(total + count);
            if (total > maximum) throw new InvalidDataException("La mise à jour dépasse la taille autorisée.");
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    internal void CleanupOldStages()
    {
        try
        {
            UpdatePackage.RejectReparsePoints(PreparationRoot);
            if (!Directory.Exists(PreparationRoot)) return;
            var preparedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var preparedPath in new[] { PreparedPathFor(false), PreparedPathFor(true) })
            {
                if (!File.Exists(preparedPath)) continue;
                UpdatePackage.RejectReparsePoints(preparedPath);
                if (new FileInfo(preparedPath).Length > 16384) return;
                var preparedId = JsonSerializer.Deserialize<PreparedUpdate>(File.ReadAllText(preparedPath))?.StageId;
                if (preparedId is not null) preparedIds.Add(preparedId);
            }
            foreach (var directory in Directory.EnumerateDirectories(PreparationRoot).Take(100))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                // A crash after committing prepared-update.json may leave an abandoned marker.
                // Never delete the package still referenced by the current ready record.
                if (preparedIds.Contains(Path.GetFileName(directory))) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                var marker = File.Exists(Path.Combine(directory, "complete")) ? Path.Combine(directory, "complete") : Path.Combine(directory, "abandoned");
                if (!File.Exists(marker) || DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(1)) continue;
                var pending = new Stack<string>(); pending.Push(directory);
                var safe = true; var entries = 0;
                while (pending.Count > 0 && safe)
                    foreach (var item in Directory.EnumerateFileSystemEntries(pending.Pop()))
                    {
                        var attributes = File.GetAttributes(item);
                        if (++entries > 2048 || (attributes & FileAttributes.ReparsePoint) != 0) { safe = false; break; }
                        if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
                    }
                if (safe) Directory.Delete(directory, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    public void Dispose()
    {
        lock (_checkGate)
        {
            if (_disposed) return;
            _disposed = true;
            _checksLifetime.Cancel();
        }
        if (_ownsClient) _client.Dispose();
    }
}
