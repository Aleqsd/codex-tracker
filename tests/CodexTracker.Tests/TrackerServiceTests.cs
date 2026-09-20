using System.Collections.Concurrent;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class TrackerServiceTests
{
    [Fact]
    public async Task ObservingAToBToAKeepsBothSnapshotsAndFollowsTheCurrentAccount()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var reader = new FakeReader();
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await using var service = new TrackerService(authPath, Options(directory), reader);
        await service.InitializeAsync();
        var firstA = Assert.Single(service.State.Accounts);
        Assert.Equal("a@example.test", firstA.Profile.Email);
        Assert.True(firstA.IsActiveInCodex);
        Assert.Equal(firstA.Profile.Id, service.State.SelectedAccountId);
        Assert.Equal(90, firstA.Snapshot!.Weekly!.RemainingPercent);

        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("b@example.test", "b"));
        await service.ImportCurrentAccountAsync();
        var b = Assert.Single(service.State.Accounts, a => a.IsActiveInCodex);
        Assert.Equal("b@example.test", b.Profile.Email);
        Assert.Equal(b.Profile.Id, service.State.SelectedAccountId);
        Assert.Equal(40, b.Snapshot!.Weekly!.RemainingPercent);
        Assert.Same(firstA.Snapshot, Assert.Single(service.State.Accounts, a => a.Profile.Id == firstA.Profile.Id).Snapshot);

        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await service.ImportCurrentAccountAsync();
        Assert.Equal(2, service.State.Accounts.Count);
        Assert.Equal(firstA.Profile.Id, Assert.Single(service.State.Accounts, a => a.IsActiveInCodex).Profile.Id);
        Assert.Equal(firstA.Profile.Id, service.State.SelectedAccountId);
        Assert.Same(b.Snapshot, Assert.Single(service.State.Accounts, a => a.Profile.Id == b.Profile.Id).Snapshot);
        Assert.Equal(new[] { "a@example.test", "b@example.test", "a@example.test" }, reader.Requests.Select(r => r.Email));
        Assert.Equal(new[] { "test-account-a", "test-account-b", "test-account-a" }, reader.Requests.Select(r => r.AccountId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public async Task MissingOrUnknownSessionClearsActiveStatusWithoutInventingUsage(string? authContent)
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var reader = new FakeReader();
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await using var service = new TrackerService(authPath, Options(directory), reader);
        await service.InitializeAsync();
        var before = Assert.Single(service.State.Accounts).Snapshot;

        if (authContent is null) File.Delete(authPath);
        else await File.WriteAllTextAsync(authPath, authContent);
        await service.RefreshAsync();

        Assert.DoesNotContain(service.State.Accounts, account => account.IsActiveInCodex);
        Assert.Same(before, Assert.Single(service.State.Accounts).Snapshot);
        Assert.Single(reader.Requests);
        Assert.False(service.State.IsBusy);
    }

    [Fact]
    public async Task FailedUsageReadPreservesLastSnapshotAndItsTimestamp()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var reader = new FakeReader();
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await using var service = new TrackerService(authPath, Options(directory), reader);
        await service.InitializeAsync();
        var snapshot = Assert.Single(service.State.Accounts).Snapshot!;
        reader.Handler = (_, _, _) => Task.FromException<AccountSnapshot>(new TrackerException("Fixture: usage unavailable"));

        await service.RefreshAsync();

        var account = Assert.Single(service.State.Accounts);
        Assert.Same(snapshot, account.Snapshot);
        Assert.Equal(snapshot.FetchedAt, account.Snapshot!.FetchedAt);
        Assert.NotNull(account.Error);
        Assert.True(account.IsStale);
        Assert.True(account.IsActiveInCodex);
        Assert.False(account.IsRefreshing);
        Assert.False(service.State.IsBusy);
    }

    [Fact]
    public async Task SelectingHistorySurvivesTokenRotationAndThenFollowsAnActualAccountChange()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await using var service = new TrackerService(authPath, Options(directory), new FakeReader());
        await service.InitializeAsync();
        var aId = Assert.Single(service.State.Accounts).Profile.Id;
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("b@example.test", "b"));
        await service.RefreshAsync();
        await service.SelectAccountAsync(aId);

        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("b@example.test", "b", "rotated"));
        await service.RefreshAsync();
        Assert.Equal(aId, service.State.SelectedAccountId);
        Assert.Equal("b@example.test", Assert.Single(service.State.Accounts, a => a.IsActiveInCodex).Profile.Email);

        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("c@example.test", "c"));
        await service.RefreshAsync();
        var active = Assert.Single(service.State.Accounts, a => a.IsActiveInCodex);
        Assert.Equal("c@example.test", active.Profile.Email);
        Assert.Equal(active.Profile.Id, service.State.SelectedAccountId);
    }

    [Fact]
    public async Task UnexpectedReaderIdentityIsRejectedAndOldSnapshotIsKept()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var reader = new FakeReader();
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await using var service = new TrackerService(authPath, Options(directory), reader);
        await service.InitializeAsync();
        var previous = Assert.Single(service.State.Accounts).Snapshot;
        reader.Handler = (_, _, _) => Task.FromResult(Snapshot("unexpected@example.test", 99));

        await service.RefreshAsync();

        var active = Assert.Single(service.State.Accounts);
        Assert.Same(previous, active.Snapshot);
        Assert.NotNull(active.Error);
        Assert.Equal("a@example.test", active.Snapshot!.Email);
    }

    [Fact]
    public async Task ReturningToACannotAcceptTheResultFromItsEarlierSessionGeneration()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var firstAStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateFirstA = new TaskCompletionSource<AccountSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newAReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aReads = 0;
        var reader = new FakeReader
        {
            Handler = (profile, _, _) =>
            {
                if (profile.Email == "b@example.test") return Task.FromResult(Snapshot(profile.Email, 60));
                if (Interlocked.Increment(ref aReads) != 1) return Task.FromResult(Snapshot(profile.Email, 20));
                firstAStarted.TrySetResult();
                return lateFirstA.Task;
            }
        };
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await using var service = new TrackerService(authPath, Options(directory) with
        {
            MonitorAuthChanges = true,
            DetectionInterval = TimeSpan.FromMilliseconds(20)
        }, reader);
        var acceptedOldResults = new ConcurrentQueue<double>();
        service.Changed += (_, _) =>
        {
            var active = service.State.Accounts.FirstOrDefault(a => a.IsActiveInCodex);
            if (active?.Snapshot?.Weekly is not { } weekly) return;
            if (active.Profile.Email == "b@example.test") bReady.TrySetResult();
            if (active.Profile.Email == "a@example.test" && weekly.UsedPercent == 20) newAReady.TrySetResult();
            if (active.Profile.Email == "a@example.test" && weekly.UsedPercent == 99) acceptedOldResults.Enqueue(weekly.UsedPercent);
        };
        var initializing = service.InitializeAsync();
        try
        {
            await firstAStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("b@example.test", "b"));
            await bReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
            await newAReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            lateFirstA.TrySetResult(Snapshot("a@example.test", 99));
            await initializing.WaitAsync(TimeSpan.FromSeconds(5));
            var active = Assert.Single(service.State.Accounts, a => a.IsActiveInCodex);
            Assert.Equal("a@example.test", active.Profile.Email);
            Assert.Equal(80, active.Snapshot!.Weekly!.RemainingPercent);
            Assert.Empty(acceptedOldResults);
            Assert.DoesNotContain(service.GetHistory(active.Profile.Id), sample => sample.WeeklyRemaining == 1);
        }
        finally
        {
            lateFirstA.TrySetResult(Snapshot("a@example.test", 99));
            try { await initializing.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task LateResultForACannotBecomeTheActiveSnapshotAfterCodexChangesToB()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateA = new TaskCompletionSource<AccountSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bDetected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader
        {
            Handler = (profile, _, _) =>
            {
                if (profile.Email == "a@example.test")
                {
                    aStarted.TrySetResult();
                    // Model a completed network reply that arrives despite cancellation.
                    return lateA.Task;
                }
                return Task.FromResult(Snapshot(profile.Email, 60));
            }
        };
        await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("a@example.test", "a"));
        await using var service = new TrackerService(authPath, Options(directory) with
        {
            MonitorAuthChanges = true,
            DetectionInterval = TimeSpan.FromMilliseconds(20)
        }, reader);
        var mismatchedSnapshots = new ConcurrentQueue<string>();
        service.Changed += (_, _) =>
        {
            var active = service.State.Accounts.FirstOrDefault(a => a.IsActiveInCodex);
            if (active?.Snapshot is { } snapshot && snapshot.Email != active.Profile.Email)
                mismatchedSnapshots.Enqueue(snapshot.Email);
            if (active?.Profile.Email != "b@example.test") return;
            bDetected.TrySetResult();
            if (active.Snapshot is not null) bReady.TrySetResult();
        };
        var initializing = service.InitializeAsync();
        try
        {
            await aStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllBytesAsync(authPath, TestFixtures.Auth("b@example.test", "b"));
            await bDetected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            lateA.TrySetResult(Snapshot("a@example.test", 10));
            await initializing.WaitAsync(TimeSpan.FromSeconds(5));
            await bReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var active = Assert.Single(service.State.Accounts, a => a.IsActiveInCodex);
            Assert.Equal("b@example.test", active.Profile.Email);
            Assert.Equal("b@example.test", active.Snapshot!.Email);
            Assert.Equal(40, active.Snapshot.Weekly!.RemainingPercent);
            Assert.Equal(active.Profile.Id, service.State.SelectedAccountId);
            Assert.Empty(mismatchedSnapshots);
        }
        finally
        {
            lateA.TrySetResult(Snapshot("a@example.test", 10));
            try { await initializing.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ObserverPersistsUsageHistoryWithoutWritingOrStoringSessionCredentials()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var original = TestFixtures.Auth("a@example.test", "observer-must-not-store-this-session");
        await File.WriteAllBytesAsync(authPath, original);
        var lastWrite = File.GetLastWriteTimeUtc(authPath);
        var options = Options(directory);
        var reader = new FakeReader();
        DateTimeOffset fetchedAt;

        // Deny writers while allowing the observer's file reads.
        using (var readOnlyLease = new FileStream(authPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var service = new TrackerService(authPath, options, reader))
        {
            await service.InitializeAsync();
            await service.RefreshAsync();
            fetchedAt = Assert.Single(service.State.Accounts).Snapshot!.FetchedAt;
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(authPath));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(authPath));
        Assert.False(Directory.Exists(Path.Combine(options.DataDirectory, "vault")));
        foreach (var file in Directory.EnumerateFiles(options.DataDirectory, "*", SearchOption.AllDirectories))
        {
            var contents = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("access_token", contents);
            Assert.DoesNotContain("refresh_token", contents);
            Assert.DoesNotContain("observer-must-not-store-this-session", contents);
        }

        File.Delete(authPath);
        await using var resumed = new TrackerService(authPath, options, reader);
        await resumed.InitializeAsync();
        var history = Assert.Single(resumed.State.Accounts);
        Assert.Equal(fetchedAt, history.Snapshot!.FetchedAt);
        Assert.Equal(90, history.Snapshot.Weekly!.RemainingPercent);
        Assert.False(history.IsActiveInCodex);
        Assert.Equal(2, reader.Requests.Count);
    }

    [Fact]
    public async Task DisposingDuringInitialReadCancelsWorkAndReleasesTheStoreWithoutChangingAuth()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        var original = TestFixtures.Auth("a@example.test", "a");
        await File.WriteAllBytesAsync(authPath, original);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readerStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader
        {
            Handler = async (profile, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { readerStopped.TrySetResult(); }
                return Snapshot(profile.Email, 10);
            }
        };
        var options = Options(directory) with
        {
            MonitorAuthChanges = true,
            DetectionInterval = TimeSpan.FromMilliseconds(20),
            AutomaticRefresh = true
        };
        using var readOnlyLease = new FileStream(authPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var service = new TrackerService(authPath, options, reader);
        var initializing = service.InitializeAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await readerStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await initializing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original, await File.ReadAllBytesAsync(authPath));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        using var reopenedStore = new ProfileStore(options.DataDirectory);
        reopenedStore.Open();
        Assert.Single(reopenedStore.LoadSettings().Accounts);
    }

    private static TrackerServiceOptions Options(TestDirectory directory) => new()
    {
        DataDirectory = directory.File("data"),
        CodexExecutablePath = directory.File("not-a-real-codex.exe"),
        AutomaticRefresh = false,
        MonitorAuthChanges = false
    };

    private static AccountSnapshot Snapshot(string email, double usedPercent) => new(email, "plus",
        [new("codex", "Codex", [new(usedPercent, 10080, DateTimeOffset.UtcNow.AddDays(2))])],
        null, null, DateTimeOffset.UtcNow);

    private sealed class FakeReader : IAccountUsageReader
    {
        public ConcurrentQueue<(string Email, string AccountId)> Requests { get; } = new();
        public Func<AccountProfile, string, CancellationToken, Task<AccountSnapshot>> Handler { get; set; } =
            (profile, _, _) => Task.FromResult(Snapshot(profile.Email, profile.Email == "a@example.test" ? 10 : 60));
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue((profile.Email, expectedAccountId));
            return Handler(profile, expectedAccountId, cancellationToken);
        }
    }
}
