using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeSourceRefreshTests
{
    private static readonly TimeSpan Settles = TimeSpan.FromMilliseconds(750);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CacheNoiseRespectsFailureBackoffButExplicitReadingsAndManualRequestsRecover(bool manual)
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var cache = Cache(directory);
        var reader = new Reader { Fail = true };
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory), claudeReader: reader);
        await service.InitializeAsync();
        Assert.Equal(1, reader.Calls);
        for (var i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(cache, "unrelated-cache-" + i);
            await Task.Delay(Settles);
            Assert.Equal(1, reader.Calls);
        }
        Assert.Equal(1, Status(service).ConsecutiveFailures);
        Assert.False(service.State.IsBusy);

        reader.Fail = false;
        if (manual) await service.RefreshAsync();
        else await File.WriteAllTextAsync(directory.File("plan-usage-history.json"), "explicit-reading-changed");
        await Until(() => reader.Calls >= 2 && !service.State.IsBusy);
        Assert.Equal(0, Status(service).ConsecutiveFailures);
        Assert.Equal(RefreshOutcome.Updated, Status(service).Outcome);

        await File.WriteAllTextAsync(cache, "cache-after-recovery");
        await Until(() => reader.Calls >= 3 && !service.State.IsBusy);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CacheSignalDuringAReadCannotScheduleAnotherPassAfterFailure(bool succeeds)
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var cache = Cache(directory);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { Fail = !succeeds, BeforeRead = async call =>
        { if (call == 1) { started.TrySetResult(); await release.Task; } } };
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory), claudeReader: reader);
        var initializing = service.InitializeAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(cache, "unrelated-during-first-pass");
            await Task.Delay(Settles);
        }
        finally { release.TrySetResult(); }
        await initializing.WaitAsync(TimeSpan.FromSeconds(5));
        if (succeeds) await Until(() => reader.Calls == 2 && !service.State.IsBusy);
        await Task.Delay(Settles);
        Assert.Equal(succeeds ? 2 : 1, reader.Calls);
        Assert.False(service.State.IsBusy);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ANewClaudeStoreInstallationIsReadAndMonitoredWithoutRestarting(bool automatic)
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var roaming = directory.File("roaming");
        var local = directory.File("local");
        Directory.CreateDirectory(roaming); Directory.CreateDirectory(local);
        var options = Options(directory) with
        {
            ClaudeDesktopUsagePaths = null,
            AutomaticRefresh = automatic, RefreshInterval = TimeSpan.FromMilliseconds(100),
            ClaudeDesktopUsagePathsProvider = () => ClaudeDesktopUsageReader.ResolvePaths(roaming, local)
        };
        await using var service = new TrackerService(directory.File("missing-auth.json"), options);
        await service.InitializeAsync();
        Assert.Null(service.State.ActiveAccount!.Snapshot);

        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_fixture"));
        var history = Path.Combine(local, "Claude-Data", "claude_fixture", "plan-usage-history.json");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        await File.WriteAllTextAsync(history, History(30));
        if (!automatic) await service.RefreshAsync();
        await Until(() => service.State.ActiveAccount!.Snapshot is not null);
        Assert.Equal(70, service.State.ActiveAccount!.Snapshot!.Weekly!.RemainingPercent);

        await File.WriteAllTextAsync(history, History(60));
        await Until(() => service.State.ActiveAccount!.Snapshot!.Weekly!.RemainingPercent == 40);
        Assert.Equal(0, Status(service).ConsecutiveFailures);
    }

    [Fact]
    public async Task CacheSignalDuringSuccessfulManualRecoveryIsRetainedWithoutRestartingTheFailingLoop()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var cache = Cache(directory);
        var reader = new Reader { Fail = true };
        await using var service = new TrackerService(directory.File("missing-auth.json"), Options(directory), claudeReader: reader);
        await service.InitializeAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Fail = false;
        reader.BeforeRead = async call => { if (call == 2) { started.TrySetResult(); await release.Task; } };
        var recovery = service.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllTextAsync(cache, "cache-during-recovery");
            await Task.Delay(Settles);
            Assert.Equal(2, reader.Calls);
        }
        finally { release.TrySetResult(); }
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => reader.Calls == 3 && !service.State.IsBusy);
        Assert.Equal(0, Status(service).ConsecutiveFailures);
    }

    [Fact]
    public async Task ExplicitDesktopPathsRemainIsolatedFromDiscovery()
    {
        using var directory = new TestDirectory();
        ClaudeFixture.SignIn(directory);
        var options = Options(directory) with
        { ClaudeDesktopUsagePathsProvider = () => throw new InvalidOperationException("Explicit fixtures must not rediscover real roots") };
        await using var service = new TrackerService(directory.File("missing-auth.json"), options, claudeReader: new Reader());
        await service.InitializeAsync();
        await service.RefreshAsync();
        Assert.Equal(RefreshOutcome.Updated, Status(service).Outcome);
    }

    private static string Cache(TestDirectory directory)
    {
        Directory.CreateDirectory(directory.File("claude-code-sessions"));
        Directory.CreateDirectory(directory.File("local-agent-mode-sessions"));
        Directory.CreateDirectory(directory.File("Cache/Cache_Data"));
        var path = directory.File("Cache/Cache_Data/data_0");
        File.WriteAllText(path, "initial-unrelated-cache");
        return path;
    }

    private static TrackerServiceOptions Options(TestDirectory directory) => new()
    {
        DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory),
        ClaudeDesktopUsagePaths = [directory.File("plan-usage-history.json")],
        AutomaticRefresh = false, MonitorAuthChanges = true, DetectionInterval = TimeSpan.FromMilliseconds(30)
    };

    private static ProviderRefreshStatus Status(TrackerService service) => service.GetRefreshStatus().Single(s => s.Provider == AccountProvider.ClaudeCode);
    private static string History(double used) => JsonSerializer.Serialize(new { version = 2, samples = new[]
    { new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), org = "33333333-3333-4333-8333-333333333333", u = new { fh = used, sd = used } } } });
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private sealed class Reader : IAccountUsageReader
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        internal volatile bool Fail;
        internal Func<int, Task>? BeforeRead;
        public async Task<AccountSnapshot> ReadAsync(AccountProfile profile, string expectedAccountId, CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls);
            if (BeforeRead is { } before) await before(call).WaitAsync(token);
            if (Fail) throw new TrackerException("Fictional unavailable source");
            return new(profile.Email, "max", [new("claude", "Claude Code", [new(30, 10080, DateTimeOffset.UtcNow.AddDays(2))])], null, null, DateTimeOffset.UtcNow);
        }
    }
}
