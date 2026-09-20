using System.Threading.Channels;
using CodexTracker.Codex;
using Xunit;

namespace CodexTracker.Tests;

public sealed class AuthFileMonitorTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ChangedContentNotifiesOnceDespiteDuplicateFileEvents()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        await File.WriteAllTextAsync(authPath, "fixture-a");
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = NewMonitor(authPath, changes);
        monitor.Start();
        await NextChangeAsync(changes);

        await File.WriteAllTextAsync(authPath, "fixture-b");
        await NextChangeAsync(changes);
        await File.WriteAllTextAsync(authPath, "fixture-b");
        await AssertNoChangeAsync(changes);
    }

    [Fact]
    public async Task AtomicReplacementAndLogoutAreDetected()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        await File.WriteAllTextAsync(authPath, "fixture-a");
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = NewMonitor(authPath, changes);
        monitor.Start();
        await NextChangeAsync(changes);

        var replacement = directory.File("auth.new");
        await File.WriteAllTextAsync(replacement, "fixture-b");
        File.Move(replacement, authPath, true);
        await NextChangeAsync(changes);
        await AssertNoChangeAsync(changes);

        File.Delete(authPath);
        await NextChangeAsync(changes);
        await AssertNoChangeAsync(changes);
    }

    [Fact]
    public async Task ObservationResumesWhenMissingParentDirectoryIsRecreated()
    {
        using var directory = new TestDirectory();
        var authDirectory = directory.File("codex");
        Directory.CreateDirectory(authDirectory);
        var authPath = Path.Combine(authDirectory, "auth.json");
        await File.WriteAllTextAsync(authPath, "fixture-a");
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = NewMonitor(authPath, changes);
        monitor.Start();
        await NextChangeAsync(changes);

        File.Delete(authPath);
        Directory.Delete(authDirectory);
        await NextChangeAsync(changes);
        await AssertNoChangeAsync(changes);

        Directory.CreateDirectory(authDirectory);
        await File.WriteAllTextAsync(authPath, "fixture-b");
        await NextChangeAsync(changes);
    }

    [Fact]
    public async Task DisposeStopsFutureCallbacks()
    {
        using var directory = new TestDirectory();
        var authPath = directory.File("auth.json");
        await File.WriteAllTextAsync(authPath, "fixture-a");
        var changes = Channel.CreateUnbounded<bool>();
        var monitor = NewMonitor(authPath, changes);
        monitor.Start();
        await NextChangeAsync(changes);
        await File.WriteAllTextAsync(authPath, "fixture-b");
        await NextChangeAsync(changes);
        await monitor.DisposeAsync();

        await File.WriteAllTextAsync(authPath, "fixture-c");
        await AssertNoChangeAsync(changes);
    }

    private static AuthFileMonitor NewMonitor(string path, Channel<bool> changes) =>
        new(path, cancellationToken =>
        {
            changes.Writer.TryWrite(true);
            return Task.CompletedTask;
        }, PollInterval);

    private static async Task NextChangeAsync(Channel<bool> changes)
    {
        using var timeout = new CancellationTokenSource(EventTimeout);
        Assert.True(await changes.Reader.ReadAsync(timeout.Token));
    }

    private static async Task AssertNoChangeAsync(Channel<bool> changes)
    {
        using var quietPeriod = new CancellationTokenSource(TimeSpan.FromMilliseconds(450));
        try
        {
            var received = await changes.Reader.WaitToReadAsync(quietPeriod.Token);
            Assert.False(received, "The monitor delivered an unexpected or duplicate change notification.");
        }
        catch (OperationCanceledException) when (quietPeriod.IsCancellationRequested) { }
    }
}
