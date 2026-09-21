using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexTracker.App.Updates;
using Xunit;

namespace CodexTracker.Tests;

public sealed partial class UpdateTests
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
    public async Task PersistentCacheUsesEtagAnd304RevalidatesItsTimestamp()
    {
        using var directory = new TestDirectory();
        var path = directory.File("release-cache.json");
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var handler = new FakeHandler(_ => ReleasesResponse("0.4.0", "\"fixture-etag\""));
        using (var client = new HttpClient(handler))
        using (var service = new UpdateService("0.3.0", client, path, () => now))
        {
            var first = await service.CheckDetailedAsync();
            Assert.Equal(UpdateCheckSource.Network, first.Source);
            Assert.True(first.IsVerifiedNow);
            var cached = await service.CheckDetailedAsync();
            Assert.Equal(UpdateCheckSource.Cached, cached.Source);
            Assert.False(cached.IsVerifiedNow);
            Assert.Single(handler.Requests);
            Assert.True(new FileInfo(path).Length < 64 * 1024);
            Assert.Empty(Directory.EnumerateFiles(directory.Root, "*.tmp"));
        }
        now = now.AddMinutes(6);
        using var secondClient = new HttpClient(new FakeHandler(request =>
        {
            Assert.Equal("\"fixture-etag\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.NotModified);
        }));
        using var reopened = new UpdateService("0.3.0", secondClient, path, () => now);
        var validated = await reopened.CheckDetailedAsync();
        Assert.Equal(UpdateCheckSource.ValidatedCache, validated.Source);
        Assert.True(validated.IsVerifiedNow);
        Assert.Equal(now, validated.VerifiedAt);
        Assert.Equal("0.4.0", validated.Release!.Version);
    }

    [Theory]
    [InlineData(403, "delta", 180)]
    [InlineData(429, "date", 240)]
    [InlineData(403, "reset", 360)]
    [InlineData(429, "both", 360)]
    [InlineData(403, "invalid", 60)]
    public async Task ServerBackoffIsPersistedAndPreventsRequestsAfterReopening(int status, string mode, int expectedSeconds)
    {
        using var directory = new TestDirectory();
        var path = directory.File("release-cache.json");
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            if (mode is "delta" or "both") response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(180));
            if (mode == "date") response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(240));
            if (mode is "reset" or "both")
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", now.AddSeconds(360).ToUnixTimeSeconds().ToString());
            }
            if (mode == "invalid") response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "999999999999999999");
            return response;
        });
        using (var client = new HttpClient(handler))
        using (var service = new UpdateService("0.3.0", client, path, () => now))
        {
            var result = await service.CheckDetailedAsync();
            Assert.False(result.IsVerifiedNow);
            Assert.Null(result.VerifiedAt);
            Assert.Equal(UpdateCheckSource.Unavailable, result.Source);
            Assert.Equal(now.AddSeconds(expectedSeconds), result.NextCheckAt);
            Assert.Contains("GitHub", result.Message);
        }
        var reopenedHandler = new FakeHandler(_ => ReleasesResponse("0.4.0"));
        using var reopenedClient = new HttpClient(reopenedHandler);
        using var reopened = new UpdateService("0.3.0", reopenedClient, path, () => now);
        Assert.Equal(now.AddSeconds(expectedSeconds), (await reopened.CheckDetailedAsync()).NextCheckAt);
        Assert.Empty(reopenedHandler.Requests);
        now = now.AddSeconds(expectedSeconds + 1);
        Assert.True((await reopened.CheckDetailedAsync()).IsVerifiedNow);
        Assert.Single(reopenedHandler.Requests);
    }

    [Fact]
    public async Task RepeatedSecondaryLimitsBackOffWithoutBlockingOrRetryingAutomatically()
    {
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var handler = new FakeHandler(_ => new(HttpStatusCode.TooManyRequests));
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client, clock: () => now);
        Assert.Equal(now.AddMinutes(1), (await service.CheckDetailedAsync()).NextCheckAt);
        Assert.False((await service.CheckDetailedAsync()).IsVerifiedNow);
        Assert.Single(handler.Requests);
        now = now.AddMinutes(1);
        Assert.Equal(now.AddMinutes(2), (await service.CheckDetailedAsync()).NextCheckAt);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("0.3.0", true)]
    [InlineData("0.4.0", false)]
    public async Task OfflineCacheKeepsOriginalFreshnessAndComparesAgainstInstalledVersion(string installedVersion, bool hasUpdate)
    {
        using var directory = new TestDirectory();
        var path = directory.File("release-cache.json");
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var verifiedAt = now;
        using (var client = new HttpClient(new FakeHandler(_ => ReleasesResponse("0.4.0"))))
        using (var service = new UpdateService("0.3.0", client, path, () => now)) await service.CheckDetailedAsync();
        now = now.AddDays(3);
        using var offlineClient = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("Fixture offline")));
        using var reopened = new UpdateService(installedVersion, offlineClient, path, () => now);
        var result = await reopened.CheckDetailedAsync();
        Assert.Equal(UpdateCheckSource.Cached, result.Source);
        Assert.False(result.IsVerifiedNow);
        Assert.Equal(verifiedAt, result.VerifiedAt);
        Assert.Equal(hasUpdate, result.Release is not null);
        Assert.DoesNotContain("dernière version", result.Message);
        await Assert.ThrowsAsync<IOException>(() => reopened.CheckAsync());
    }

    [Fact]
    public async Task ConcurrentChecksShareOneRequestAndOneCancelledWaitDoesNotCancelTheOthers()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var respond = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            return await respond.Task.WaitAsync(token);
        }));
        using var service = new UpdateService("0.3.0", client);
        using var cancelled = new CancellationTokenSource();
        var first = service.CheckDetailedAsync(cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.CheckDetailedAsync();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        respond.SetResult(ReleasesResponse("0.4.0"));
        Assert.Equal("0.4.0", (await second.WaitAsync(TimeSpan.FromSeconds(5))).Release!.Version);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PreCancelledChecksAndDisposalDoNotLeaveRequestsWaiting()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Not reached");
        }));
        using var service = new UpdateService("0.3.0", client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckDetailedAsync(new CancellationToken(true)));
        Assert.Equal(0, calls);
        var running = service.CheckDetailedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("foreign")]
    [InlineData("nullpage")]
    public async Task InvalidOrUntrustedPersistentCacheIsIgnored(string corruption)
    {
        using var directory = new TestDirectory();
        var path = directory.File("release-cache.json");
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        using (var client = new HttpClient(new FakeHandler(_ => ReleasesResponse("0.4.0"))))
        using (var service = new UpdateService("0.3.0", client, path, () => now)) await service.CheckDetailedAsync();
        var original = await File.ReadAllTextAsync(path);
        var content = corruption switch
        {
            "oversized" => new string(' ', 64 * 1024 + 1),
            "foreign" => original.Replace("https://github.com", "https://example.test", StringComparison.Ordinal),
            "nullpage" => "{\"Schema\":2,\"VerifiedAt\":\"2026-09-20T12:00:00Z\",\"Pages\":[null]}",
            _ => "{broken"
        };
        await File.WriteAllTextAsync(path, content);
        var handler = new FakeHandler(request =>
        {
            Assert.Empty(request.Headers.IfNoneMatch);
            return ReleasesResponse("0.5.0");
        });
        using var freshClient = new HttpClient(handler);
        using var reopened = new UpdateService("0.3.0", freshClient, path, () => now);
        Assert.Equal("0.5.0", (await reopened.CheckDetailedAsync()).Release!.Version);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PaginationFailureCannotOverwritePreviouslyVerifiedCache()
    {
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var call = 0;
        var handler = new FakeHandler(_ =>
        {
            call++;
            if (call == 1) return ReleasesResponse("0.4.0");
            if (call == 3) return new(HttpStatusCode.ServiceUnavailable);
            var response = ReleasesResponse("0.9.0");
            response.Headers.Add("Link", "<https://api.github.com/repos/Aleqsd/codex-tracker/releases?page=2&per_page=100>; rel=\"next\"");
            return response;
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client, clock: () => now);
        var previous = await service.CheckDetailedAsync();
        now = now.AddMinutes(6);
        var failed = await service.CheckDetailedAsync();
        Assert.Equal("0.4.0", failed.Release!.Version);
        Assert.Equal(previous.VerifiedAt, failed.VerifiedAt);
        Assert.False(failed.IsVerifiedNow);
        Assert.Equal(3, call);
    }

    [Fact]
    public async Task PaginationRevalidatesEachPageWithItsOwnEtag()
    {
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var revalidate = false;
        var handler = new FakeHandler(request =>
        {
            var secondPage = request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal);
            var etag = secondPage ? "\"page-two\"" : "\"page-one\"";
            if (revalidate)
            {
                Assert.Equal(etag, Assert.Single(request.Headers.IfNoneMatch).ToString());
                return new(HttpStatusCode.NotModified);
            }
            var response = ReleasesResponse(secondPage ? "0.9.0-rc.1" : "0.4.0", etag);
            if (!secondPage) response.Headers.Add("Link", "<https://api.github.com/repos/Aleqsd/codex-tracker/releases?page=2&per_page=100>; rel=\"next\"");
            return response;
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client, clock: () => now);
        Assert.Equal("0.9.0-rc.1", (await service.CheckDetailedAsync()).Release!.Version);
        revalidate = true; now = now.AddMinutes(6);
        var result = await service.CheckDetailedAsync();
        Assert.Equal(UpdateCheckSource.ValidatedCache, result.Source);
        Assert.Equal(now, result.VerifiedAt);
        Assert.Equal("0.9.0-rc.1", result.Release!.Version);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task NotModifiedResponseCanIntroduceANewNextPageAndEtag()
    {
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var call = 0;
        var handler = new FakeHandler(request =>
        {
            call++;
            if (call == 1) return ReleasesResponse("0.4.0", "\"original\"");
            if (call == 2)
            {
                Assert.Equal("\"original\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
                var response = new HttpResponseMessage(HttpStatusCode.NotModified);
                response.Headers.ETag = new EntityTagHeaderValue("\"updated\"");
                response.Headers.Add("Link", "<https://api.github.com/repos/Aleqsd/codex-tracker/releases?page=2&per_page=100>; rel=\"next\"");
                return response;
            }
            if (call == 3)
            {
                Assert.Contains("page=2", request.RequestUri!.Query);
                Assert.Empty(request.Headers.IfNoneMatch);
                return ReleasesResponse("0.9.0", "\"page-two\"");
            }
            Assert.Equal(call == 4 ? "\"updated\"" : "\"page-two\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return new(HttpStatusCode.NotModified);
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client, clock: () => now);
        Assert.Equal("0.4.0", (await service.CheckDetailedAsync()).Release!.Version);
        now = now.AddMinutes(6);
        Assert.Equal("0.9.0", (await service.CheckDetailedAsync()).Release!.Version);
        now = now.AddMinutes(6);
        Assert.Equal("0.9.0", (await service.CheckDetailedAsync()).Release!.Version);
        Assert.Equal(5, call);
    }

    [Theory]
    [InlineData("incoherent")]
    [InlineData("far-future")]
    [InlineData("clock-backwards")]
    public async Task ImplausiblePersistentDeadlinesCannotBlockManualVerification(string corruption)
    {
        using var directory = new TestDirectory();
        var path = directory.File("release-cache.json");
        var now = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var recorded = corruption == "clock-backwards" ? now.AddHours(2) : now;
        var content = JsonSerializer.Serialize(new
        {
            Schema = 2, VerifiedAt = (DateTimeOffset?)null,
            NextCheckAt = corruption == "clock-backwards" ? recorded.AddMinutes(3) : DateTimeOffset.MaxValue,
            Failures = corruption == "incoherent" ? 0 : 1,
            RateLimited = corruption != "incoherent", Pages = Array.Empty<object>(), RecordedAt = recorded
        });
        await File.WriteAllTextAsync(path, content);
        var handler = new FakeHandler(_ => ReleasesResponse("0.4.0"));
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client, path, () => now);
        Assert.Null(service.ReadCachedCheck());
        Assert.True((await service.CheckDetailedAsync()).IsVerifiedNow);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedReleaseResponseIsNotSavedAsVerified()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = ReleasesResponse("0.4.0");
            response.Content.Headers.ContentLength = 4 * 1024 * 1024 + 1;
            return response;
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client);
        var result = await service.CheckDetailedAsync();
        Assert.False(result.IsVerifiedNow);
        Assert.Null(result.VerifiedAt);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task ForeignPaginationIsRejectedWithoutFollowingTheLink()
    {
        var handler = new FakeHandler(_ =>
        {
            var response = ReleasesResponse("0.4.0");
            response.Headers.Add("Link", "<https://example.test/releases?page=2&per_page=100>; rel=\"next\"");
            return response;
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService("0.3.0", client);
        Assert.Equal(UpdateCheckSource.Unavailable, (await service.CheckDetailedAsync()).Source);
        Assert.Single(handler.Requests);
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

    private static HttpResponseMessage ReleasesResponse(string version, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { ReleaseRow(version) })) };
        if (etag is not null) response.Headers.ETag = new EntityTagHeaderValue(etag);
        return response;
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }

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
