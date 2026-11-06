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

    [Fact]
    public async Task UnchangedPollingDoesNotReopenTheFileButExplicitChecksStillVerifyItsContent()
    {
        using var directory = new TestDirectory();
        var path = directory.File("auth.json");
        await File.WriteAllTextAsync(path, "fixture-a");
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = new AuthFileMonitor(path, _ =>
        {
            changes.Writer.TryWrite(true);
            return Task.CompletedTask;
        }, PollInterval, true, TimeSpan.FromMinutes(1), watchFiles: false);
        monitor.Start();
        await NextChangeAsync(changes);

        // Metadata remains readable while the content is exclusively held. Ordinary polling
        // must not manufacture an "unavailable" change by opening an unchanged file again.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await AssertNoChangeAsync(changes);
            monitor.RequestCheck();
            await NextChangeAsync(changes);
        }
        // A transient read failure is retried at the short cadence, not cached for a minute.
        await NextChangeAsync(changes);
    }

    [Fact]
    public async Task VerificationPollFindsChangedBytesEvenWhenMetadataAndWatcherEventsAreUnchanged()
    {
        using var directory = new TestDirectory();
        var path = directory.File("auth.json");
        await File.WriteAllTextAsync(path, "fixture-a");
        var written = File.GetLastWriteTimeUtc(path);
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = new AuthFileMonitor(path, _ =>
        {
            changes.Writer.TryWrite(true);
            return Task.CompletedTask;
        }, PollInterval, true, TimeSpan.FromMilliseconds(900), watchFiles: false);
        monitor.Start();
        await NextChangeAsync(changes);
        await File.WriteAllTextAsync(path, "fixture-b");
        File.SetLastWriteTimeUtc(path, written);

        await NextChangeAsync(changes);
        await AssertNoChangeAsync(changes);
    }

    [Fact]
    public async Task OversizeFileIsBoundedAndBecomesReadableAgainAfterReplacement()
    {
        using var directory = new TestDirectory();
        var path = directory.File("auth.json");
        using (var oversized = File.Create(path)) oversized.SetLength(16L * 1024 * 1024);
        var changes = Channel.CreateUnbounded<bool>();
        await using var monitor = NewMonitor(path, changes);
        monitor.Start();
        await NextChangeAsync(changes);
        await AssertNoChangeAsync(changes);
        await File.WriteAllTextAsync(path, "fixture-small");
        await NextChangeAsync(changes);
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
