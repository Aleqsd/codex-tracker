using System.Collections.Concurrent;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeCodeTrackerTests
{
    private static TrackerServiceOptions Options(TestDirectory directory, bool monitor = false) => new()
    {
        DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory), ClaudeDesktopUsagePaths = [],
        AutomaticRefresh = false, MonitorAuthChanges = monitor, DetectionInterval = TimeSpan.FromMilliseconds(30)
    };
    private sealed class Reader(string bucket) : IAccountUsageReader
    {
        internal ConcurrentQueue<string> Reads = new();
        internal Func<AccountProfile, CancellationToken, Task<AccountSnapshot>>? Handler;
        internal AccountSnapshot Snapshot(AccountProfile profile, double used = 30) => new(profile.Email, "pro",
            [new(bucket, bucket, [new(used, 10080, DateTimeOffset.UtcNow.AddDays(2)), new(used, 300, DateTimeOffset.UtcNow.AddHours(2))])], null, null, DateTimeOffset.UtcNow);
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken cancellationToken)
        { Reads.Enqueue(profile.Email); return Handler?.Invoke(profile, cancellationToken) ?? Task.FromResult(Snapshot(profile)); }
    }

    [Fact]
    public async Task SameEmailHasTwoIndependentProvidersAndBothActiveAccountsPersistAcrossRestart()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var auth = directory.File("auth.json"); File.WriteAllBytes(auth, TestFixtures.Auth());
        var codex = new Reader("codex"); var claude = new Reader("claude"); var options = Options(directory);
        Guid[] ids;
        await using (var service = new TrackerService(auth, options, codex, claude))
        {
            await service.InitializeAsync();
            Assert.Equal(2, service.State.ActiveAccounts.Count); Assert.Equal(2, service.State.Accounts.Count);
            Assert.Equal(AccountProvider.Codex, service.State.ActiveAccount!.Profile.Provider);
            Assert.All(service.State.Accounts, a => Assert.Equal("demo@example.test", a.Profile.Email));
            Assert.Single(service.State.Accounts, a => a.IsActiveInCodex); Assert.Single(service.State.Accounts, a => a.IsActiveInClaudeCode);
            Assert.All(service.State.Accounts, a => Assert.NotNull(a.Snapshot));
            ids = service.State.Accounts.Select(a => a.Profile.Id).ToArray(); Assert.NotEqual(ids[0], ids[1]);
            foreach (var id in ids) await Assert.ThrowsAsync<TrackerException>(() => service.RemoveAccountAsync(id));
        }
        File.Delete(auth); File.Delete(directory.File(".claude.json")); File.Delete(directory.File(".credentials.json"));
        await using var restarted = new TrackerService(auth, options, codex, claude); await restarted.InitializeAsync();
        Assert.Equal(ids, restarted.State.Accounts.Select(a => a.Profile.Id)); Assert.Empty(restarted.State.ActiveAccounts);
        Assert.All(restarted.State.Accounts, a => Assert.NotNull(a.Snapshot));
        var stored = File.ReadAllText(Path.Combine(options.DataDirectory, "settings.json"));
        Assert.Contains("ClaudeCode", stored); Assert.DoesNotContain("fixture-secret", stored);
    }

    [Fact]
    public async Task ClaudeAloneWorksWithoutCodexAndFailureKeepsLastObservation()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var claude = new Reader("claude");
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory), new Reader("codex"), claude);
        await service.InitializeAsync();
        var first = Assert.Single(service.State.Accounts); Assert.True(first.IsActive); Assert.True(first.IsActiveInClaudeCode);
        Assert.Equal(first, service.State.ActiveAccount);
        claude.Handler = (_, _) => throw new IOException("fixture-private-error"); await service.RefreshAsync();
        Assert.Equal(first.Snapshot, service.State.ActiveAccount!.Snapshot); Assert.NotNull(service.State.ActiveAccount.Error);
        Assert.DoesNotContain("fixture-private-error", service.State.ActiveAccount.Error!);
    }

    [Fact]
    public async Task ClaudeAloneRespectsTheRefreshIntervalAfterEveryAutomaticRead()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var claude = new Reader("claude");
        var times = new ConcurrentQueue<long>();
        var thirdRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        claude.Handler = (profile, _) =>
        {
            times.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
            if (times.Count >= 3) thirdRead.TrySetResult();
            return Task.FromResult(claude.Snapshot(profile));
        };
        var interval = TimeSpan.FromSeconds(2);
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory) with
        {
            AutomaticRefresh = true, RefreshIntervalProvider = () => interval
        }, new Reader("codex"), claude);
        await service.InitializeAsync();
        await thirdRead.Task.WaitAsync(TimeSpan.FromSeconds(12));
        var reads = times.ToArray();
        // Compare automatic reads, excluding the initial read and its startup work.
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(reads[1], reads[2]);
        Assert.True(elapsed >= interval - TimeSpan.FromMilliseconds(150), $"Automatic reads were only {elapsed} apart.");
    }

    [Fact]
    public async Task FrequentNativeClaudeObservationsDoNotPostponeCodexPeriodicReads()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var auth = directory.File("auth.json"); File.WriteAllBytes(auth, TestFixtures.Auth());
        var usage = directory.File("plan-usage-history.json"); File.WriteAllText(usage, "{}");
        var codex = new Reader("codex"); var claude = new Reader("claude");
        await using var service = new TrackerService(auth, Options(directory, monitor: true) with
        {
            ClaudeDesktopUsagePaths = [usage], AutomaticRefresh = true,
            RefreshIntervalProvider = () => TimeSpan.FromSeconds(2)
        }, codex, claude);
        await service.InitializeAsync(); await Task.Delay(500);
        var baseline = codex.Reads.Count; var claudeBaseline = claude.Reads.Count;
        var nextCodex = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        codex.Handler = (profile, _) => { if (codex.Reads.Count > baseline) nextCodex.TrySetResult(); return Task.FromResult(codex.Snapshot(profile)); };
        using var stop = new CancellationTokenSource();
        var observations = Task.Run(async () =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                await File.WriteAllTextAsync(usage, System.Text.Json.JsonSerializer.Serialize(new { version = 2, observation = i }), stop.Token);
                await Task.Delay(100, stop.Token);
            }
        });
        try
        {
            await nextCodex.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(claude.Reads.Count >= claudeBaseline + 2, "Native Claude observations must refresh independently during the interval.");
        }
        finally
        {
            stop.Cancel();
            try { await observations; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task SwitchingClaudeDuringSlowReadIgnoresOldResponseAndDoesNotCancelCodex()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory, "a@example.test");
        var auth = directory.File("auth.json"); File.WriteAllBytes(auth, TestFixtures.Auth("codex@example.test"));
        var codex = new Reader("codex"); var claude = new Reader("claude");
        await using var service = new TrackerService(auth, Options(directory), codex, claude); await service.InitializeAsync();
        var first = service.State.Accounts.Single(a => a.IsActiveInClaudeCode);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        claude.Handler = async (profile, _) => { if (profile.Email == "a@example.test") { started.TrySetResult(); await release.Task; } return claude.Snapshot(profile, 90); };
        var old = service.RefreshAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        ClaudeFixture.SignIn(directory, "b@example.test", ClaudeFixture.Second);
        await service.RefreshAsync(); release.SetResult(); await old;
        Assert.Equal("b@example.test", service.State.Accounts.Single(a => a.IsActiveInClaudeCode).Profile.Email);
        Assert.Equal(first.Snapshot, service.State.Accounts.Single(a => a.Profile.Id == first.Profile.Id).Snapshot);
        Assert.True(service.State.Accounts.Single(a => a.IsActiveInCodex).IsActive);
        Assert.Equal(2, service.State.ActiveAccounts.Count); Assert.False(service.State.IsBusy);
    }

    [Fact]
    public async Task NativeCaptureSignalRefreshesOnlyClaudeAndDoesNotPollCodexForEveryTurn()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var auth = directory.File("auth.json"); File.WriteAllBytes(auth, TestFixtures.Auth());
        var options = Options(directory, monitor: true); var codex = new Reader("codex");
        await using var service = new TrackerService(auth, options, codex); await service.InitializeAsync();
        // Initial watcher callbacks settle before measuring independence.
        await Task.Delay(500); var count = codex.Reads.Count;
        var collector = new ClaudeCodeObservations(options.DataDirectory, options.ClaudeLocation!); var session = Guid.NewGuid();
        await collector.CaptureAsync(ClaudeFixture.Input(session), true, DateTimeOffset.UtcNow);
        await collector.CaptureAsync(ClaudeFixture.Input(session, 25), false, DateTimeOffset.UtcNow);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        while (service.State.Accounts.Single(a => a.IsActiveInClaudeCode).Snapshot is null && DateTimeOffset.UtcNow < deadline) await Task.Delay(30);
        var active = service.State.Accounts.Single(a => a.IsActiveInClaudeCode);
        Assert.Equal(75, active.Snapshot!.Short!.RemainingPercent); Assert.Equal(count, codex.Reads.Count);
    }
}
