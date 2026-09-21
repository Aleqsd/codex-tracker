using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CodexTracker.App;
using CodexTracker.App.Updates;
using Xunit;

namespace CodexTracker.Tests;

public sealed partial class UpdateTests
{
    [Fact]
    public async Task PreparedDownloadSurvivesRestartAndDoesNotNeedNetworkToInstall()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip);
        PreparedUpdate pending;
        using (var service = new UpdateService("0.3.0", client, directory.File("release-cache.json")))
        {
            pending = await service.PrepareAsync(Release(zip.Length));
            Assert.False(service.IsPreparing);
            Assert.Equal(pending, service.Prepared);
            Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.tmp"));
        }
        using var offline = new HttpClient(new FakeHandler(_ => throw new IOException("Offline fixture")));
        using var reopened = new UpdateService("0.3.0", offline, directory.File("release-cache.json"));
        Assert.Equal(pending, await reopened.LoadPreparedAsync());
        Assert.NotNull(await reopened.ClaimPreparedAsync(true, default));
        // A helper failure / power cut after this point may not cause a reboot loop.
        using var nextBoot = new UpdateService("0.3.0", offline, directory.File("release-cache.json"));
        Assert.Null(await nextBoot.ClaimPreparedAsync(true, default));
        Assert.NotNull(await nextBoot.ClaimPreparedAsync(false, default));
    }

    [Fact]
    public async Task ConcurrentPreparationDownloadsOnlyOnePackage()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        var requests = 0;
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            Interlocked.Increment(ref requests);
            await Task.Delay(20, token);
            return PackageResponse(request, zip);
        }));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        var ready = await Task.WhenAll(service.PrepareAsync(Release(zip.Length)), service.PrepareAsync(Release(zip.Length)));
        Assert.Equal(ready[0], ready[1]);
        Assert.Equal(2, requests); // checksum + ZIP, shared by both callers
    }

    [Fact]
    public async Task ChangedPayloadIsRejectedBeforeClaimingAndCanBeDownloadedAgain()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip);
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        var pending = await service.PrepareAsync(Release(zip.Length));
        await File.WriteAllTextAsync(service.PreparedExecutable(pending), "MZ-tampered");
        Assert.Null(await service.ClaimPreparedAsync(true, default));
        Assert.Null(service.Prepared);
        var retry = await service.PrepareAsync(Release(zip.Length));
        Assert.NotEqual(pending.StageId, retry.StageId);
        Assert.Equal(retry, await service.LoadPreparedAsync());
    }

    [Fact]
    public async Task InstalledOrOlderPreparedVersionIsNeverAppliedAgain()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip);
        using (var service = new UpdateService("0.3.0", client, directory.File("cache.json")))
            await service.PrepareAsync(Release(zip.Length));
        using var updated = new UpdateService("0.4.0", client, directory.File("cache.json"));
        Assert.Null(await updated.LoadPreparedAsync());
        Assert.False(File.Exists(directory.File("prepared-update.json")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("not-a-stage")]
    public async Task PendingRecordCannotPointOutsideTheUpdateDirectory(string id)
    {
        using var directory = new TestDirectory();
        using var client = PackageClient([]);
        await File.WriteAllTextAsync(directory.File("prepared-update.json"), JsonSerializer.Serialize(
            new PreparedUpdate(Release(10), id, new string('a', 64), DateTimeOffset.UtcNow)));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        Assert.Null(await service.LoadPreparedAsync());
    }

    [Fact]
    public async Task FailedChecksumNeverPublishesAReadyUpdate()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip, new string('0', 64));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(Release(zip.Length)));
        Assert.Null(service.Prepared);
        Assert.False(service.IsPreparing);
        Assert.False(File.Exists(directory.File("prepared-update.json")));
        Assert.Single(Directory.GetFiles(directory.Root, "abandoned", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CleanupCannotRemoveReadyPackageAfterCrashBetweenCommitAndMarkerRemoval()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip);
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        var pending = await service.PrepareAsync(Release(zip.Length));
        var marker = Path.Combine(directory.Root, pending.StageId, "abandoned");
        await File.WriteAllTextAsync(marker, "abandoned");
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-2));
        service.CleanupOldStages();
        Assert.Equal(pending, await service.LoadPreparedAsync());
    }

    [Fact]
    public async Task FailureDownloadingNewerVersionPreservesPreviousReadyPackage()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        var fail = false;
        using var client = new HttpClient(new FakeHandler(request => fail
            ? new(HttpStatusCode.ServiceUnavailable) : PackageResponse(request, zip)));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        var first = await service.PrepareAsync(Release(zip.Length));
        fail = true;
        var newer = JsonSerializer.Deserialize<UpdateRelease>(JsonSerializer.Serialize(Release(zip.Length)).Replace("0.4.0", "0.5.0"))!;
        await Assert.ThrowsAsync<IOException>(() => service.PrepareAsync(newer));
        Assert.Equal(first, service.Prepared);
        Assert.Equal(first, await service.LoadPreparedAsync());
    }

    [Fact]
    public async Task DisabledAutomaticUpdatesMakeNoRequestsAndEnabledUpdatesWaitSixHours()
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.Parse("2026-09-21T12:00:00Z");
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        var checks = 0;
        using var client = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            { checks++; return PackageReleaseResponse(zip.Length); }
            return PackageResponse(request, zip);
        }));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"), () => now);
        using var automatic = new AutomaticUpdater(service, () => now);
        await automatic.TickAsync();
        Assert.Equal(0, checks);
        automatic.SetEnabled(true);
        await automatic.TickAsync();
        Assert.NotNull(service.Prepared);
        Assert.Equal(now.AddHours(6), automatic.NextCheck);
        now = now.AddHours(5);
        await automatic.TickAsync();
        Assert.Equal(1, checks);
        now = now.AddHours(1);
        await automatic.TickAsync();
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task DisablingDuringDownloadCancelsWithoutPublishingReadyState()
    {
        using var directory = new TestDirectory();
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com") return PackageReleaseResponse(123);
            downloadStarted.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        }));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        using var automatic = new AutomaticUpdater(service);
        automatic.SetEnabled(true);
        var tick = automatic.TickAsync();
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        automatic.SetEnabled(false);
        await tick;
        Assert.Null(service.Prepared);
        Assert.False(service.IsPreparing);
        Assert.False(File.Exists(directory.File("prepared-update.json")));
    }

    [Fact]
    public async Task OfflineCheckDoesNotLosePreparedUpdateOrHammerTheNetwork()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using (var packageClient = PackageClient(zip))
        using (var initial = new UpdateService("0.3.0", packageClient, directory.File("cache.json")))
            await initial.PrepareAsync(Release(zip.Length));
        var requests = 0;
        var now = DateTimeOffset.UtcNow;
        using var offline = new HttpClient(new FakeHandler(_ => { requests++; throw new HttpRequestException("Offline"); }));
        using var service = new UpdateService("0.3.0", offline, directory.File("cache.json"), () => now);
        await service.LoadPreparedAsync();
        using var automatic = new AutomaticUpdater(service, () => now);
        automatic.SetEnabled(true);
        await automatic.TickAsync();
        await automatic.TickAsync();
        Assert.NotNull(service.Prepared);
        Assert.Equal(1, requests);
        Assert.True(automatic.NextCheck > now);
    }

    [Fact]
    public void ExistingPreferencesGetAutomaticUpdateDefaultsWithoutBeingRewritten()
    {
        using var directory = new TestDirectory();
        var path = directory.File("preferences.json");
        const string previous = "{\"themeMode\":\"Dark\",\"refreshMinutes\":5}";
        File.WriteAllText(path, previous);
        var preferences = new PreferencesStore(true, directory.Root);
        Assert.True(preferences.Current.DownloadUpdatesAutomatically);
        Assert.True(preferences.Current.InstallUpdatesAtStartup);
        Assert.Equal(5, preferences.Current.RefreshMinutes);
        Assert.Equal(previous, File.ReadAllText(path));
        preferences.Update(p => p with { DownloadUpdatesAutomatically = false, InstallUpdatesAtStartup = false });
        var reopened = new PreferencesStore(true, directory.Root);
        Assert.False(reopened.Current.DownloadUpdatesAutomatically);
        Assert.False(reopened.Current.InstallUpdatesAtStartup);
    }

    private static HttpResponseMessage PackageResponse(HttpRequestMessage request, byte[] zip) => new(HttpStatusCode.OK)
    {
        Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new StringContent(Convert.ToHexStringLower(SHA256.HashData(zip)) + "  CodexTracker-0.4.0-win-x64.zip")
            : new ByteArrayContent(zip)
    };

    private static HttpResponseMessage PackageReleaseResponse(long size) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new[] { new { tag_name = "v0.4.0", assets = new[] {
            new { name = "CodexTracker-0.4.0-win-x64.zip", size, browser_download_url = Release(size).DownloadUrl.AbsoluteUri },
            new { name = "CodexTracker-0.4.0-win-x64.zip.sha256", size = 100L, browser_download_url = Release(size).ChecksumUrl.AbsoluteUri }
        } } }))
    };
}
