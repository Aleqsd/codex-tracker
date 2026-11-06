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
        new([directory.File("plan-usage-history.json")], _ =>
        {
            changes.Writer.TryWrite(true);
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(30));

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
