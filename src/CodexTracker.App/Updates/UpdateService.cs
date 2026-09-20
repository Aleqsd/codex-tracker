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

public sealed record UpdateRelease(string Version, string Tag, Uri NotesUrl, Uri DownloadUrl, Uri ChecksumUrl, long Size);
public sealed record UpdateOutcome(DateTimeOffset At, bool Success, string Message);

public sealed class UpdateService : IDisposable
{
    private const string Repository = "/Aleqsd/codex-tracker";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    public string CurrentVersion { get; }
    internal static string UpdatesRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker", "updates");

    public UpdateService() : this(typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0", null) { }
    internal UpdateService(string version, HttpClient? client)
    {
        CurrentVersion = SemanticVersion.Parse(version)?.Text ?? "0.0.0";
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null
        }) { Timeout = TimeSpan.FromMinutes(5) };
        if (_client.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("Le service de mise à jour n’utilise aucun jeton d’authentification.");
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        UpdateRelease? best = null;
        // The list endpoint includes prereleases. GitHub's /latest endpoint would omit this project's previews.
        for (var page = 1; page <= 5; page++)
        {
            var uri = new Uri($"https://api.github.com/repos{Repository}/releases?per_page=100&page={page}");
            using var response = await GetAsync(uri, false, timeout.Token);
            var bytes = await ReadBoundedAsync(response, 4 * 1024 * 1024, timeout.Token);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("La réponse de GitHub est invalide.");
            var candidate = SelectRelease(document.RootElement, CurrentVersion);
            if (candidate is not null && (best is null || SemanticVersion.Parse(candidate.Version)!.CompareTo(SemanticVersion.Parse(best.Version)) > 0)) best = candidate;
            if (document.RootElement.GetArrayLength() < 100) break;
        }
        return best;
    }

    internal static UpdateRelease? SelectRelease(JsonElement releases, string currentVersion)
    {
        var current = SemanticVersion.Parse(currentVersion) ?? throw new InvalidDataException("Version actuelle invalide.");
        UpdateRelease? best = null;
        foreach (var row in releases.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || row.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True ||
                !row.TryGetProperty("tag_name", out var tagNode) || tagNode.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            var tag = tagNode.GetString()!;
            var version = SemanticVersion.Parse(tag);
            if (version is null || version.CompareTo(current) <= 0 || best is not null && version.CompareTo(SemanticVersion.Parse(best.Version)) <= 0) continue;
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
                best = new(version.Text, tag, new Uri($"https://github.com{Repository}/releases/tag/{Uri.EscapeDataString(tag)}"), download, checksum, size);
        }
        return best;
    }

    public async Task StageAndLaunchAsync(UpdateRelease release, int processId, CancellationToken cancellationToken = default)
    {
        if (processId != Environment.ProcessId) throw new InvalidOperationException("Le processus de mise à jour ne correspond pas à cette application.");
        var target = Environment.ProcessPath ?? throw new IOException("L’application en cours est introuvable.");
        if (!string.Equals(Path.GetFileName(target), "CodexTracker.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Utilisez la version installée ou portable pour effectuer la mise à jour.");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(target)!, "CodexTracker.dll")))
            throw new IOException("La mise à jour automatique nécessite la version autonome installée ou portable.");
        UpdatePackage.RejectReparsePoints(target);
        UpdatePackage.RejectReparsePoints(UpdatesRoot);
        var stage = Path.Combine(UpdatesRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var helperStarted = false;
        try
        {
            var executable = await StagePackageAsync(release, stage, cancellationToken);
            var helper = Path.Combine(stage, "CodexTracker.UpdateHelper.exe");
            File.Copy(target, helper, false);
            using var current = Process.GetCurrentProcess();
            var manifest = new UpdateManifest(target, executable, await UpdatePackage.HashAsync(target, cancellationToken),
                await UpdatePackage.HashAsync(executable, cancellationToken), processId, current.StartTime.ToUniversalTime().Ticks);
            var manifestPath = Path.Combine(stage, "update.json");
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken);
            var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
            info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(manifestPath);
            using var child = Process.Start(info) ?? throw new IOException("Le programme de mise à jour n’a pas démarré.");
            helperStarted = true;
            var ready = Path.Combine(stage, "helper.ready");
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                while (!File.Exists(ready))
                {
                    if (child.HasExited) throw new IOException("Le programme de mise à jour n’a pas pu préparer l’installation.");
                    await Task.Delay(100, wait.Token);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await File.WriteAllTextAsync(Path.Combine(stage, "cancel"), "cancel", CancellationToken.None);
                throw;
            }
            // The caller now closes this app normally. The helper never closes Codex or kills this app.
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

    private static void CleanupOldStages()
    {
        try
        {
            UpdatePackage.RejectReparsePoints(UpdatesRoot);
            if (!Directory.Exists(UpdatesRoot)) return;
            foreach (var directory in Directory.EnumerateDirectories(UpdatesRoot).Take(100))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
