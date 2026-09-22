using System.Net;
using System.Text.Json;
using CodexTracker.App.Updates;
using Microsoft.Win32;
using Xunit;

namespace CodexTracker.Tests;

public sealed partial class UpdateTests
{
    [Fact]
    public async Task StableIsDefaultAndRejectsBothGithubAndSemanticPreviews()
    {
        using var client = new HttpClient(new FakeHandler(_ => MixedChannelsResponse()));
        using var service = new UpdateService("0.3.0", client);
        Assert.False(service.IncludePrereleases);
        var stable = (await service.CheckDetailedAsync()).Release!;
        Assert.Equal("0.4.0", stable.Version);
        Assert.False(stable.IsPrerelease);
        service.SetIncludePrereleases(true);
        var preview = (await service.CheckDetailedAsync()).Release!;
        Assert.Equal("0.6.0-rc.1", preview.Version);
        Assert.True(preview.IsPrerelease);
    }

    [Fact]
    public async Task NumericGithubPrereleaseRetainsItsPreviewClassification()
    {
        using var client = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(new[] { ReleaseRow("0.5.0", prerelease: true) })) }));
        using var service = new UpdateService("0.3.0", client);
        Assert.Null((await service.CheckDetailedAsync()).Release);
        service.SetIncludePrereleases(true);
        Assert.True((await service.CheckDetailedAsync()).Release!.IsPrerelease);
    }

    [Fact]
    public async Task ChannelCachesAndEtagsStaySeparateAcrossRestarts()
    {
        using var directory = new TestDirectory();
        var now = DateTimeOffset.Parse("2026-09-22T12:00:00Z");
        var path = directory.File("release-cache.json");
        using var client = new HttpClient(new FakeHandler(request =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            var response = MixedChannelsResponse();
            response.Headers.ETag = new("\"all-releases\"");
            return response;
        }));
        using (var service = new UpdateService("0.3.0", client, path, () => now))
        {
            Assert.Equal("0.4.0", (await service.CheckDetailedAsync()).Release!.Version);
            service.SetIncludePrereleases(true);
            Assert.Null(service.ReadCachedCheck());
            Assert.Equal("0.6.0-rc.1", (await service.CheckDetailedAsync()).Release!.Version);
            service.SetIncludePrereleases(false);
            Assert.Equal("0.4.0", service.ReadCachedCheck()!.Release!.Version);
        }
        using var offline = new HttpClient(new FakeHandler(_ => throw new IOException("Offline")));
        using var reopened = new UpdateService("0.3.0", offline, path, () => now);
        Assert.Equal("0.4.0", reopened.ReadCachedCheck()!.Release!.Version);
        reopened.SetIncludePrereleases(true);
        Assert.Equal("0.6.0-rc.1", reopened.ReadCachedCheck()!.Release!.Version);
    }

    [Fact]
    public async Task PreviewRequestCompletingAfterOptOutCannotPublishOrReturnAPreview()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            { started.SetResult(); await finish.Task.WaitAsync(token); }
            return MixedChannelsResponse();
        }));
        using var service = new UpdateService("0.3.0", client);
        service.SetIncludePrereleases(true);
        var pending = service.CheckDetailedAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.SetIncludePrereleases(false);
        Assert.Equal("0.4.0", (await service.CheckDetailedAsync()).Release!.Version);
        finish.SetResult();
        Assert.Equal("0.4.0", (await pending).Release!.Version);
        Assert.Equal("0.4.0", service.ReadCachedCheck()!.Release!.Version);
    }

    [Fact]
    public async Task OldAmbiguousCachesAreDiscardedBeforeAnyConditionalRequest()
    {
        using var directory = new TestDirectory();
        var path = directory.File("release-cache.json");
        var now = DateTimeOffset.UtcNow;
        using var client = new HttpClient(new FakeHandler(request =>
        { Assert.Empty(request.Headers.IfNoneMatch); return ReleasesResponse("0.4.0", "\"fixture\""); }));
        using (var service = new UpdateService("0.3.0", client, path, () => now))
            await service.CheckDetailedAsync();
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("\"Schema\":3", "\"Schema\":2"));
        using var reopened = new UpdateService("0.3.0", client, path, () => now);
        Assert.Null(reopened.ReadCachedCheck());
        Assert.True((await reopened.CheckDetailedAsync()).IsVerifiedNow);
    }

    [Fact]
    public async Task PreviewPreparedBeforeOptOutCannotBeClaimedAtTheNextBoot()
    {
        using var directory = new TestDirectory();
        var path = directory.File("cache.json");
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-preview"));
        using var client = PackageClient(zip);
        using (var service = new UpdateService("0.3.0", client, path))
        {
            service.SetIncludePrereleases(true);
            await service.PrepareAsync(Release(zip.Length) with { IsPrerelease = true });
            service.SetIncludePrereleases(false);
            Assert.Null(service.Prepared);
            Assert.Null(await service.ClaimPreparedAsync(true, default));
        }
        using var nextBoot = new UpdateService("0.3.0", client, path);
        Assert.Null(await nextBoot.LoadPreparedAsync());
        Assert.Null(await nextBoot.ClaimPreparedAsync(true, default));
        nextBoot.SetIncludePrereleases(true);
        Assert.NotNull(await nextBoot.LoadPreparedAsync());
    }

    [Fact]
    public async Task OldPreparedFileIsNotMistakenForAVerifiedStableRelease()
    {
        using var directory = new TestDirectory();
        var path = directory.File("cache.json");
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip);
        using var service = new UpdateService("0.3.0", client, path);
        await service.PrepareAsync(Release(zip.Length));
        File.Move(directory.File("prepared-update-stable.json"), directory.File("prepared-update.json"));
        Assert.Null(await service.LoadPreparedAsync());
        Assert.Null(await service.ClaimPreparedAsync(true, default));
    }

    [Fact]
    public async Task CleanupPreservesReadyPackagesOnBothChannels()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-new"));
        using var client = PackageClient(zip);
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        var stable = await service.PrepareAsync(Release(zip.Length));
        service.SetIncludePrereleases(true);
        var preview = await service.PrepareAsync(Release(zip.Length) with { IsPrerelease = true });
        foreach (var pending in new[] { stable, preview })
        {
            var marker = Path.Combine(directory.Root, pending.StageId, "abandoned");
            await File.WriteAllTextAsync(marker, "interrupted-cleanup");
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddDays(-2));
        }
        service.CleanupOldStages();
        Assert.Equal(preview, await service.LoadPreparedAsync());
        service.SetIncludePrereleases(false);
        Assert.Equal(stable, await service.LoadPreparedAsync());
    }

    [Fact]
    public async Task OptOutDuringPreparationCannotPublishThePreview()
    {
        using var directory = new TestDirectory();
        var zip = Zip(("CodexTracker.exe", "MZ-fixture-preview"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new AsyncHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".zip", StringComparison.Ordinal))
            { started.SetResult(); await finish.Task.WaitAsync(token); }
            return PackageResponse(request, zip);
        }));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        service.SetIncludePrereleases(true);
        var pending = service.PrepareAsync(Release(zip.Length) with { IsPrerelease = true });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.SetIncludePrereleases(false);
        finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(service.Prepared);
        Assert.Null(await service.ClaimPreparedAsync(true, default));
    }

    [Fact]
    public async Task StablePreparationRejectsAnExplicitPreviewBeforeNetworkAccess()
    {
        using var directory = new TestDirectory();
        using var client = new HttpClient(new FakeHandler(_ => throw new InvalidOperationException("No network")));
        using var service = new UpdateService("0.3.0", client, directory.File("cache.json"));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(Release(123) with { IsPrerelease = true }));
    }

    [Fact]
    public async Task ChannelChangeResetsAutomaticScheduleWithoutWaitingSixHours()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(_ =>
        { calls++; return new(HttpStatusCode.OK) { Content = new StringContent("[]") }; }));
        using var service = new UpdateService("0.3.0", client, clock: () => now);
        using var automatic = new AutomaticUpdater(service, () => now);
        automatic.SetEnabled(true);
        await automatic.TickAsync();
        Assert.Equal(now.AddHours(6), automatic.NextCheck);
        service.SetIncludePrereleases(true);
        await automatic.TickAsync();
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptOutDuringHelperStartupCancelsInstallationBeforeTheAppExits(bool helperAlreadyReady)
    {
        var cancelled = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateService.WaitForHelperReadyAsync(
            () => helperAlreadyReady, () => false,
            () => throw new OperationCanceledException("Release channel changed"),
            () => { cancelled = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.True(cancelled);
    }

    [Theory]
    [InlineData(true, "1.0.0+fixture", "1.0.0")]
    [InlineData(true, "1.1.0-rc.1", "1.1.0-rc.1")]
    [InlineData(false, "1.0.0", "0.8.4")]
    [InlineData(true, "not-a-version", "0.8.4")]
    public void InstalledVersionMetadataOnlyChangesForTheMatchingInstallation(bool installedCopy, string productVersion, string expected)
    {
        using var directory = new TestDirectory();
        var keyPath = @"Software\CodexTracker.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue("InstallLocation", installedCopy ? directory.Root : directory.File("different"));
            key.SetValue("DisplayVersion", "0.8.4");
            UpdateRegistration.Refresh(key, directory.File("CodexTracker.exe"), productVersion);
            Assert.Equal(expected, key.GetValue("DisplayVersion"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
    }

    private static HttpResponseMessage MixedChannelsResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new[]
        {
            ReleaseRow("0.4.0"), ReleaseRow("0.5.0", prerelease: true), ReleaseRow("0.6.0-rc.1", prerelease: false)
        }))
    };
}
