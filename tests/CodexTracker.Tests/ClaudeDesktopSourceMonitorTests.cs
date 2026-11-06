using System.Threading.Channels;
using CodexTracker.Codex;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeDesktopSourceMonitorTests
{
    private const string Account = "11111111-1111-4111-8111-111111111111";
    private const string Organization = "22222222-2222-4222-8222-222222222222";
    private const string Session = "local_33333333-3333-4333-8333-333333333333";

    [Fact]
    public async Task SessionAndCacheEventsAreCoalescedAndUnrelatedFilesDoNotInvalidate()
    {
        using var directory = new TestDirectory();
        var sessions = Directory.CreateDirectory(directory.File($"claude-code-sessions/{Account}/{Organization}"));
        var cache = Directory.CreateDirectory(directory.File("Cache/Cache_Data"));
        Directory.CreateDirectory(directory.File("local-agent-mode-sessions"));
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = Monitor(directory, changes);
        monitor.Start();

        await File.WriteAllTextAsync(Path.Combine(sessions.FullName, Session + ".json"), "fixture-private-content-not-json");
        await File.WriteAllTextAsync(Path.Combine(cache.FullName, "index"), "fixture-cache");
        await File.WriteAllTextAsync(Path.Combine(cache.FullName, "data_0"), "fixture-cache");
        await Next(changes);
        await Quiet(changes);

        await File.WriteAllTextAsync(Path.Combine(sessions.FullName, "conversation.jsonl"), "fixture-private-conversation");
        await File.WriteAllTextAsync(Path.Combine(cache.FullName, "unrelated.txt"), "fixture");
        await File.WriteAllTextAsync(directory.File("settings.json"), "fixture");
        await Quiet(changes);
    }

    [Fact]
    public async Task MissingRootsAndAtomicSessionRenamesAreObserved()
    {
        using var directory = new TestDirectory();
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = Monitor(directory, changes);
        monitor.Start();
        var sessions = Directory.CreateDirectory(directory.File($"claude-code-sessions/{Account}/{Organization}"));
        var path = Path.Combine(sessions.FullName, Session + ".json");
        await File.WriteAllTextAsync(path, "fixture");
        await Next(changes);
        await Quiet(changes);

        var temporary = directory.File("replacement.tmp");
        await File.WriteAllTextAsync(temporary, "fixture-new");
        File.Move(temporary, path, true);
        await Next(changes);
        await Quiet(changes);
    }

    [Fact]
    public async Task AgentMetadataIsObservedAndDisposalStopsCallbacks()
    {
        using var directory = new TestDirectory();
        var agent = Directory.CreateDirectory(directory.File($"local-agent-mode-sessions/{Account}/{Organization}/{Session}/.claude"));
        Directory.CreateDirectory(directory.File("claude-code-sessions"));
        Directory.CreateDirectory(directory.File("Cache/Cache_Data"));
        var changes = Channel.CreateUnbounded<bool>();
        var monitor = Monitor(directory, changes);
        monitor.Start();
        var config = Path.Combine(agent.FullName, ".claude.json");
        await File.WriteAllTextAsync(config, "fixture-native-metadata");
        await Next(changes);
        await Quiet(changes);
        await monitor.DisposeAsync();
        await File.WriteAllTextAsync(config, "fixture-changed");
        await Quiet(changes);
    }

    private static ClaudeDesktopSourceMonitor Monitor(TestDirectory directory, Channel<bool> changes) =>
        new([directory.File("plan-usage-history.json")], (_, _) =>
        {
            changes.Writer.TryWrite(true);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(30));

    [Fact]
    public async Task CacheSignalsStayDistinctAndUpdatingRootsDetachesOldWatchers()
    {
        using var directory = new TestDirectory();
        using var replacement = new TestDirectory();
        foreach (var root in new[] { directory, replacement })
        {
            Directory.CreateDirectory(root.File($"claude-code-sessions/{Account}/{Organization}"));
            Directory.CreateDirectory(root.File("local-agent-mode-sessions"));
            Directory.CreateDirectory(root.File("Cache/Cache_Data"));
        }
        var changes = Channel.CreateUnbounded<ClaudeDesktopChange>();
        await using var monitor = new ClaudeDesktopSourceMonitor([directory.File("plan-usage-history.json")], (kind, _) =>
        { changes.Writer.TryWrite(kind); return Task.CompletedTask; }, TimeSpan.FromMilliseconds(30));
        monitor.Start();
        await File.WriteAllTextAsync(directory.File("Cache/Cache_Data/data_0"), "cache-only");
        Assert.Equal(ClaudeDesktopChange.Cache, await NextKind());
        await File.WriteAllTextAsync(directory.File($"claude-code-sessions/{Account}/{Organization}/{Session}.json"), "metadata");
        await File.WriteAllTextAsync(directory.File("Cache/Cache_Data/data_0"), "mixed-cache");
        Assert.Equal(ClaudeDesktopChange.Metadata | ClaudeDesktopChange.Cache, await NextKind());

        monitor.UpdatePaths([replacement.File("plan-usage-history.json")]);
        await File.WriteAllTextAsync(directory.File("Cache/Cache_Data/data_0"), "old-detached-cache");
        await Task.Delay(500);
        Assert.False(changes.Reader.TryRead(out _));
        await File.WriteAllTextAsync(replacement.File("Cache/Cache_Data/data_0"), "new-cache");
        Assert.Equal(ClaudeDesktopChange.Cache, await NextKind());
        MarkWatcherFailed("Cache_Data");
        Assert.Equal(ClaudeDesktopChange.Cache, await NextKind());
        MarkWatcherFailed("claude-code-sessions");
        Assert.Equal(ClaudeDesktopChange.Metadata, await NextKind());

        void MarkWatcherFailed(string suffix)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var sync = typeof(ClaudeDesktopSourceMonitor).GetField("_sync", flags)!.GetValue(monitor)!;
            lock (sync)
            {
                var targets = (System.Collections.IEnumerable)typeof(ClaudeDesktopSourceMonitor).GetField("_targets", flags)!.GetValue(monitor)!;
                var target = targets.Cast<object>().Single(t => ((string)t.GetType().GetProperty("Path", flags)!.GetValue(t)!).EndsWith(suffix));
                target.GetType().GetField("Failed", flags)!.SetValue(target, true);
            }
        }

        async Task<ClaudeDesktopChange> NextKind()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await changes.Reader.ReadAsync(timeout.Token);
        }
    }

    private static async Task Next(Channel<bool> changes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.True(await changes.Reader.ReadAsync(timeout.Token));
    }

    private static async Task Quiet(Channel<bool> changes)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { Assert.False(await changes.Reader.WaitToReadAsync(timeout.Token)); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
    }
}
