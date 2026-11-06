using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class RefreshCoordinationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    // AuthFileMonitor debounces for 300 ms. Let a real file notification settle while
    // the fake reader is deliberately blocked; no provider process or UI is involved.
    private static readonly TimeSpan MonitorSettles = TimeSpan.FromMilliseconds(750);

    [Fact]
    public async Task StartupMonitorsDoNotRepeatTheInitialCollection()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var auth = directory.File("auth.json");
        File.WriteAllBytes(auth, TestFixtures.Auth());
        var signal = CreateSignal(directory);
        var codex = new Reader("codex");
        var claude = new Reader("claude");
        await using var service = new TrackerService(auth, Options(directory, monitor: true), codex, claude);

        await service.InitializeAsync();
        await Task.Delay(MonitorSettles);

        Assert.Equal(1, codex.Calls);
        Assert.Equal(1, claude.Calls);
        Assert.Equal("{\"revision\":0}", File.ReadAllText(signal));
    }

    [Fact]
    public async Task SeveralNativeSignalsDuringAReadProduceOneFollowUpWithTheLatestObservation()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var signal = CreateSignal(directory);
        var claude = new Reader("claude");
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory, monitor: true), new Reader("codex"), claude);
        await service.InitializeAsync();
        await Task.Delay(MonitorSettles);
        var baseline = claude.Calls;
        var started = NewSignal();
        var release = NewSignal();
        var followUp = NewSignal();
        var used = 30d;
        claude.Handler = async (profile, call, _) =>
        {
            var snapshot = claude.Snapshot(profile, Volatile.Read(ref used));
            if (call == baseline + 1) { started.TrySetResult(); await release.Task; }
            else followUp.TrySetResult();
            return snapshot;
        };
        var pending = service.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(Timeout);
            Volatile.Write(ref used, 60);
            await File.WriteAllTextAsync(signal, "{\"revision\":1}");
            await Task.Delay(MonitorSettles);
            Volatile.Write(ref used, 80);
            await File.WriteAllTextAsync(signal, "{\"revision\":2}");
            await Task.Delay(MonitorSettles);
            Assert.Equal(baseline + 1, claude.Calls);
        }
        finally { release.TrySetResult(); }

        await pending.WaitAsync(Timeout);
        await followUp.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => !service.State.IsBusy && service.State.ActiveAccount?.Snapshot?.Short?.RemainingPercent == 20);
        await Task.Delay(MonitorSettles);
        Assert.Equal(baseline + 2, claude.Calls);
        Assert.Equal(20, service.State.ActiveAccount!.Snapshot!.Short!.RemainingPercent);
    }

    [Fact]
    public async Task ConcurrentManualRequestsShareOneReadForEachProvider()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var auth = directory.File("auth.json");
        File.WriteAllBytes(auth, TestFixtures.Auth());
        var codex = new Reader("codex");
        var claude = new Reader("claude");
        await using var service = new TrackerService(auth, Options(directory), codex, claude);
        await service.InitializeAsync();
        var codexStarted = NewSignal();
        var claudeStarted = NewSignal();
        var release = NewSignal();
        codex.Handler = async (profile, _, _) => { codexStarted.TrySetResult(); await release.Task; return codex.Snapshot(profile); };
        claude.Handler = async (profile, _, _) => { claudeStarted.TrySetResult(); await release.Task; return claude.Snapshot(profile); };
        var requests = Enumerable.Range(0, 12).Select(_ => service.RefreshAsync()).ToArray();
        try
        {
            await Task.WhenAll(codexStarted.Task, claudeStarted.Task).WaitAsync(Timeout);
            await Task.Delay(MonitorSettles);
            Assert.Equal(2, codex.Calls);
            Assert.Equal(2, claude.Calls);
        }
        finally { release.TrySetResult(); }

        await Task.WhenAll(requests).WaitAsync(Timeout);
        Assert.Equal(2, codex.Calls);
        Assert.Equal(2, claude.Calls);
        Assert.False(service.State.IsBusy);
    }

    [Fact]
    public async Task SuspensionDiscardsAPendingSignalAndAnOldResponseBeforeResume()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var signal = CreateSignal(directory);
        var claude = new Reader("claude");
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory, monitor: true), new Reader("codex"), claude);
        await service.InitializeAsync();
        await Task.Delay(MonitorSettles);
        var baseline = claude.Calls;
        var original = service.State.ActiveAccount!.Snapshot;
        var started = NewSignal();
        var release = NewSignal();
        claude.Handler = async (profile, call, _) =>
        {
            if (call == baseline + 1)
            {
                var obsolete = claude.Snapshot(profile, 97);
                started.TrySetResult();
                await release.Task; // Simulate a source that returns even after cancellation.
                return obsolete;
            }
            return claude.Snapshot(profile, 80);
        };
        var pending = service.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(Timeout);
            await File.WriteAllTextAsync(signal, "{\"revision\":1}");
            await Task.Delay(MonitorSettles);
            await service.SuspendAsync();
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Timeout);
        await Task.Delay(MonitorSettles);
        Assert.Equal(baseline + 1, claude.Calls);
        Assert.Same(original, service.State.ActiveAccount!.Snapshot);
        Assert.False(service.State.IsBusy);

        await service.ResumeAsync().WaitAsync(Timeout);
        await Task.Delay(MonitorSettles);
        Assert.Equal(baseline + 2, claude.Calls);
        Assert.Equal(20, service.State.ActiveAccount!.Snapshot!.Short!.RemainingPercent);
        Assert.DoesNotContain(service.GetHistory(service.State.ActiveAccount.Profile.Id), sample => sample.ShortRemaining == 3);
    }

    [Fact]
    public async Task PendingSignalFromAnOldIdentityCannotPublishItsLateObservation()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory, "first@example.test");
        var signal = CreateSignal(directory);
        var claude = new Reader("claude");
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory, monitor: true), new Reader("codex"), claude);
        await service.InitializeAsync();
        await Task.Delay(MonitorSettles);
        var oldAccount = service.State.ActiveAccount!;
        var started = NewSignal();
        var release = NewSignal();
        claude.Handler = async (profile, _, _) =>
        {
            if (profile.Email == "first@example.test")
            {
                var obsolete = claude.Snapshot(profile, 97);
                started.TrySetResult();
                await release.Task;
                return obsolete;
            }
            return claude.Snapshot(profile, 80);
        };
        var pending = service.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(Timeout);
            await File.WriteAllTextAsync(signal, "{\"revision\":1}");
            await Task.Delay(MonitorSettles);
            ClaudeFixture.SignIn(directory, "second@example.test", ClaudeFixture.Second);
            await service.RefreshAsync().WaitAsync(Timeout);
        }
        finally { release.TrySetResult(); }
        await pending.WaitAsync(Timeout);
        await Task.Delay(MonitorSettles);

        Assert.Equal("second@example.test", service.State.ActiveAccount!.Profile.Email);
        Assert.Equal(20, service.State.ActiveAccount.Snapshot!.Short!.RemainingPercent);
        Assert.Same(oldAccount.Snapshot, service.State.Accounts.Single(a => a.Profile.Id == oldAccount.Profile.Id).Snapshot);
        Assert.DoesNotContain(service.GetHistory(oldAccount.Profile.Id), sample => sample.ShortRemaining == 3);
    }

    private static TrackerServiceOptions Options(TestDirectory directory, bool monitor = false) => new()
    {
        DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory), ClaudeDesktopUsagePaths = [],
        AutomaticRefresh = false, MonitorAuthChanges = monitor, DetectionInterval = TimeSpan.FromMilliseconds(30), RequestTimeout = TimeSpan.FromSeconds(10)
    };

    private static string CreateSignal(TestDirectory directory)
    {
        var path = Path.Combine(directory.File("data"), "claude-observations", "latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"revision\":0}");
        return path;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var stop = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(20, stop.Token);
    }

    private sealed class Reader(string bucket) : IAccountUsageReader
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Func<AccountProfile, int, CancellationToken, Task<AccountSnapshot>>? Handler;
        public AccountSnapshot Snapshot(AccountProfile profile, double used = 30) => new(profile.Email, "pro",
            [new(bucket, bucket, [new(used, 10080, DateTimeOffset.UtcNow.AddDays(2)), new(used, 300, DateTimeOffset.UtcNow.AddHours(2))])], null, null, DateTimeOffset.UtcNow);
        public Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls);
            return Handler?.Invoke(profile, call, token) ?? Task.FromResult(Snapshot(profile));
        }
    }
}
