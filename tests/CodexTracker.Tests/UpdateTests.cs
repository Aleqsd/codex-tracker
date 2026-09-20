using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTracker.App.Updates;
using Xunit;

namespace CodexTracker.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2", 1)]
    [InlineData("1.0.0", "1.0.0-rc.9", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("v1.0.0+fixture", "1.0.0", 0)]
    public void ReleasesUseSemanticOrdering(string left, string right, int sign) =>
        Assert.Equal(sign, Math.Sign(SemanticVersion.Parse(left)!.CompareTo(SemanticVersion.Parse(right))));

    [Fact]
    public async Task CheckIncludesPreviewsAndRequiresBothOfficialAssets()
    {
        var releases = new[]
        {
            ReleaseRow("0.4.0"), ReleaseRow("0.5.0-rc.2"), ReleaseRow("0.9.0", draft: true),
            ReleaseRow("1.0.0", host: "example.test"), ReleaseRow("2.0.0", includeChecksum: false)
        };
        var handler = new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases)) });
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client);

        var release = await service.CheckAsync();

        Assert.Equal("0.5.0-rc.2", release!.Version);
        Assert.Equal("v0.5.0-rc.2", release.Tag);
        Assert.Equal("https://github.com/Aleqsd/codex-tracker/releases/tag/v0.5.0-rc.2", release.NotesUrl.AbsoluteUri);
        Assert.Single(handler.Requests);
        Assert.All(handler.Requests, r => { Assert.Null(r.Authorization); Assert.False(r.HasCookie); });
    }

    [Fact]
    public async Task RateLimitsAreReportedWithoutRequestingAuthentication()
    {
        var handler = new FakeHandler(_ => new(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client);
        var exception = await Assert.ThrowsAsync<IOException>(() => service.CheckAsync());
        Assert.Contains("GitHub", exception.Message);
        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task ValidChecksumAndArchiveStageOnlyTheWindowsExecutable()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"), ("README.md", "fixture-docs"));
        using var client = PackageClient(zip);
        using var service = new UpdateService("0.3.0", client);
        var path = await service.StagePackageAsync(Release(zip.Length), directory.Root, CancellationToken.None);
        Assert.Equal("MZ-fixture-new", await File.ReadAllTextAsync(path));
        Assert.Equal(directory.File("payload" + Path.DirectorySeparatorChar + "CodexTracker.exe"), path);
        Assert.False(File.Exists(directory.File("payload" + Path.DirectorySeparatorChar + "README.md")));
    }

    [Fact]
    public async Task FailedChecksumCannotProduceAnExecutable()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip, new string('0', 64));
        using var service = new UpdateService("0.3.0", client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.StagePackageAsync(Release(zip.Length), directory.Root, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.exe", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("C:/escape.exe")]
    [InlineData("/escape.exe")]
    [InlineData("sub/../../escape.exe")]
    [InlineData("CodexTracker.exe:stream")]
    public async Task MaliciousZipPathsAreRejectedBeforeExtraction(string maliciousName)
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"), (maliciousName, "fixture"));
        using var client = PackageClient(zip);
        using var service = new UpdateService("0.3.0", client);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.StagePackageAsync(Release(zip.Length), directory.Root, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ForeignDownloadUriIsRejectedBeforeAnyNetworkRequest()
    {
        using var directory = new TestDirectory();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("No request expected"));
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client);
        var release = Release(123) with { DownloadUrl = new("https://example.test/CodexTracker-0.4.0-win-x64.zip") };
        await Assert.ThrowsAsync<InvalidDataException>(() => service.StagePackageAsync(release, directory.Root, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnexpectedRedirectHostIsRejected()
    {
        using var directory = new TestDirectory();
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("https://example.test/payload");
            return response;
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client);
        await Assert.ThrowsAsync<IOException>(() => service.StagePackageAsync(Release(123), directory.Root, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SuccessfulInstallReplacesExecutableAndLaunchesIt()
    {
        using var directory = new TestDirectory();
        var target = directory.File("CodexTracker.exe");
        var staged = directory.File("new.exe");
        await File.WriteAllTextAsync(target, "MZ-fixture-old");
        await File.WriteAllTextAsync(staged, "MZ-fixture-new");
        var launches = 0;
        await UpdateInstaller.InstallAsync(target, staged, await UpdatePackage.HashAsync(target), await UpdatePackage.HashAsync(staged),
            async (path, verify, _) =>
            {
                launches++;
                Assert.True(verify);
                Assert.Equal("MZ-fixture-new", await File.ReadAllTextAsync(path));
                return true;
            });
        Assert.Equal(1, launches);
        Assert.Equal("MZ-fixture-new", await File.ReadAllTextAsync(target));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, ".CodexTracker-*"));
    }

    [Fact]
    public async Task FailedStartupRollsBackExecutableAndRelaunchesPreviousVersion()
    {
        using var directory = new TestDirectory();
        var target = directory.File("CodexTracker.exe");
        var staged = directory.File("new.exe");
        await File.WriteAllTextAsync(target, "MZ-fixture-old");
        await File.WriteAllTextAsync(staged, "MZ-fixture-new");
        var launches = new List<bool>();
        var oldHash = await UpdatePackage.HashAsync(target);
        var newHash = await UpdatePackage.HashAsync(staged);
        var error = await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.InstallAsync(target, staged, oldHash, newHash,
            async (path, verify, _) =>
            {
                launches.Add(verify);
                Assert.Equal(verify ? "MZ-fixture-new" : "MZ-fixture-old", await File.ReadAllTextAsync(path));
                return !verify;
            }));
        Assert.Contains("restaurée", error.Message);
        Assert.Equal(new[] { true, false }, launches);
        Assert.Equal("MZ-fixture-old", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task FilesChangedAfterStagingPreventReplacement()
    {
        using var directory = new TestDirectory();
        var target = directory.File("CodexTracker.exe");
        var staged = directory.File("new.exe");
        await File.WriteAllTextAsync(target, "MZ-fixture-current");
        await File.WriteAllTextAsync(staged, "MZ-fixture-new");
        await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.InstallAsync(target, staged, new string('0', 64), new string('0', 64),
            (_, _, _) => throw new InvalidOperationException("No process should launch")));
        Assert.Equal("MZ-fixture-current", await File.ReadAllTextAsync(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureBeforeReplacementRelaunchesTheVerifiedOriginalApplication(bool stagedFileMissing)
    {
        using var directory = new TestDirectory();
        var target = directory.File("CodexTracker.exe");
        var staged = directory.File("new.exe");
        await File.WriteAllTextAsync(target, "MZ-fixture-old");
        if (!stagedFileMissing) await File.WriteAllTextAsync(staged, "MZ-fixture-unexpected");
        var oldHash = await UpdatePackage.HashAsync(target);
        var launches = 0;
        var error = await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.InstallAsync(target, staged, oldHash, new string('0', 64),
            async (path, verify, _) =>
            {
                launches++;
                Assert.False(verify);
                Assert.Equal("MZ-fixture-old", await File.ReadAllTextAsync(path));
                return true;
            }));
        Assert.Equal(1, launches);
        Assert.Contains("relancée", error.Message);
        Assert.Equal(oldHash, await UpdatePackage.HashAsync(target));
        Assert.Empty(Directory.EnumerateFiles(directory.Root, ".CodexTracker-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRelaunchAfterRollbackPointsToRestoredTargetInsteadOfConsumedBackup(bool launchThrows)
    {
        using var directory = new TestDirectory();
        var target = directory.File("CodexTracker.exe");
        var staged = directory.File("new.exe");
        await File.WriteAllTextAsync(target, "MZ-fixture-old");
        await File.WriteAllTextAsync(staged, "MZ-fixture-new");
        var oldHash = await UpdatePackage.HashAsync(target);
        var newHash = await UpdatePackage.HashAsync(staged);
        var launches = new List<bool>();
        var error = await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.InstallAsync(target, staged, oldHash, newHash,
            (path, verify, _) =>
            {
                launches.Add(verify);
                if (!verify && launchThrows) throw new IOException("Fixture: relaunch failed");
                return Task.FromResult(false);
            }));
        Assert.Equal(new[] { true, false }, launches);
        Assert.Equal(oldHash, await UpdatePackage.HashAsync(target));
        Assert.Contains("restaurée", error.Message);
        Assert.Contains("manuellement", error.Message);
        Assert.Contains(target, error.Message);
        Assert.DoesNotContain("secours", error.Message);
        Assert.Empty(Directory.EnumerateFiles(directory.Root, ".CodexTracker-previous-*"));
    }

    [Fact]
    public void HelperCannotAcceptAnArbitraryManifestPath()
    {
        using var directory = new TestDirectory();
        Assert.Throws<IOException>(() => UpdateBootstrap.ValidateStageFile(directory.File("update.json"), "update.json"));
    }

    private static UpdateRelease Release(long size) => new("0.4.0", "v0.4.0", new("https://github.com/Aleqsd/codex-tracker/releases/tag/v0.4.0"),
        new("https://github.com/Aleqsd/codex-tracker/releases/download/v0.4.0/CodexTracker-0.4.0-win-x64.zip"),
        new("https://github.com/Aleqsd/codex-tracker/releases/download/v0.4.0/CodexTracker-0.4.0-win-x64.zip.sha256"), size);

    private static object ReleaseRow(string version, bool draft = false, string host = "github.com", bool includeChecksum = true)
    {
        var name = $"CodexTracker-{version}-win-x64.zip";
        var assets = new List<object> { new { name, size = 123, browser_download_url = $"https://{host}/Aleqsd/codex-tracker/releases/download/v{version}/{name}" } };
        if (includeChecksum) assets.Add(new { name = name + ".sha256", size = 100, browser_download_url = $"https://{host}/Aleqsd/codex-tracker/releases/download/v{version}/{name}.sha256" });
        return new { tag_name = "v" + version, draft, prerelease = version.Contains('-'), assets };
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var entry in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry.Path).Open());
                writer.Write(entry.Content);
            }
        return memory.ToArray();
    }

    private static HttpClient PackageClient(byte[] zip, string? hash = null) => new(new FakeHandler(request =>
        new(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent((hash ?? Convert.ToHexStringLower(SHA256.HashData(zip))) + "  CodexTracker-0.4.0-win-x64.zip")
                : new ByteArrayContent(zip)
        }));

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<(Uri Uri, AuthenticationHeaderValue? Authorization, bool HasCookie)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization, request.Headers.Contains("Cookie")));
            return Task.FromResult(response(request));
        }
    }
}
