using System.Security.Cryptography;
using System.Threading.Channels;

namespace CodexTracker.Codex;

/// <summary>
/// Observes a Codex-owned credential file without changing it. Only a content digest is retained.
/// Callbacks are serialized and should import local metadata quickly, scheduling network work separately.
/// </summary>
public sealed class AuthFileMonitor : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly AsyncLocal<AuthFileMonitor?> CallingMonitor = new();
    private readonly string _authPath;
    private readonly string _directory;
    private readonly Func<CancellationToken, Task> _onChanged;
    private readonly TimeSpan _pollInterval;
    private readonly bool _signalInitial;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _checks = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private Task? _poller;
    private bool _started;
    private bool _disposed;
    private bool _watcherFailed;
    private bool _hasBaseline;
    private string? _fingerprint;

    public AuthFileMonitor(string authPath, Func<CancellationToken, Task> onChanged,
        TimeSpan? pollInterval = null, bool signalInitial = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authPath);
        ArgumentNullException.ThrowIfNull(onChanged);
        _authPath = Path.GetFullPath(authPath);
        _directory = Path.GetDirectoryName(_authPath) ?? throw new ArgumentException("A full file path is required.", nameof(authPath));
        _onChanged = onChanged;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        if (_pollInterval <= TimeSpan.Zero || _pollInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _signalInitial = signalInitial;
    }

    /// <summary>Starts observation once. By default the first stable check also calls the callback.</summary>
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            if (!_signalInitial)
            {
                // A silent start establishes its baseline before returning, so a subsequent write is observed.
                _fingerprint = ReadInitialFingerprint();
                _hasBaseline = true;
            }
            _started = true;
            var token = _lifetime.Token;
            _worker = Task.Run(() => ObserveAsync(token));
            _poller = Task.Run(() => PollAsync(token));
        }
        RequestCheck();
    }

    /// <summary>Queues one coalesced check; it never forces a callback for unchanged bytes.</summary>
    public void RequestCheck() => _checks.Writer.TryWrite(true);

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) RequestCheck();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _checks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // A fixed debounce bounds the delay even during continuous writes or watcher event storms.
                await Task.Delay(Debounce, cancellationToken).ConfigureAwait(false);
                while (_checks.Reader.TryRead(out _)) { }
                EnsureWatcher();
                var next = await ReadFingerprintAsync(cancellationToken).ConfigureAwait(false);
                if (_hasBaseline && string.Equals(_fingerprint, next, StringComparison.Ordinal)) continue;
                if (!_hasBaseline && !_signalInitial)
                {
                    _fingerprint = next;
                    _hasBaseline = true;
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var previousMonitor = CallingMonitor.Value;
                try
                {
                    CallingMonitor.Value = this;
                    await _onChanged(cancellationToken).ConfigureAwait(false);
                    _fingerprint = next;
                    _hasBaseline = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Do not retain exception text: a consumer exception might contain credentials.
                    // Leave the previous digest in place so the next poll retries this change.
                }
                finally { CallingMonitor.Value = previousMonitor; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<string> ReadFingerprintAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_authPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            try { return Convert.ToHexString(digest); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "missing"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }

    private string ReadInitialFingerprint()
    {
        try
        {
            using var stream = new FileStream(_authPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var digest = SHA256.HashData(stream);
            try { return Convert.ToHexString(digest); }
            finally { CryptographicOperations.ZeroMemory(digest); }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "missing"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }

    private void EnsureWatcher()
    {
        // Watch the nearest existing ancestor until the actual profile directory becomes available.
        var watchDirectory = _directory;
        while (!Directory.Exists(watchDirectory))
        {
            var parent = Path.GetDirectoryName(watchDirectory);
            if (parent is null || parent == watchDirectory) return;
            watchDirectory = parent;
        }
        lock (_sync)
        {
            if (_disposed) return;
            if (!_watcherFailed && _watcher is not null && string.Equals(_watcher.Path, watchDirectory, StringComparison.OrdinalIgnoreCase)) return;
            _watcher?.Dispose();
            _watcher = null;
            _watcherFailed = false;
            try
            {
                var watcher = new FileSystemWatcher(watchDirectory)
                {
                    Filter = "*",
                    IncludeSubdirectories = !string.Equals(watchDirectory, _directory, StringComparison.OrdinalIgnoreCase),
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };
                watcher.Changed += FileChanged;
                watcher.Created += FileChanged;
                watcher.Deleted += FileChanged;
                watcher.Renamed += FileRenamed;
                watcher.Error += WatcherError;
                _watcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher?.Dispose();
                _watcher = null;
                // Polling remains available even when the filesystem watcher cannot be established.
            }
        }
    }

    private bool AffectsAuth(string path) => string.Equals(path, _authPath, StringComparison.OrdinalIgnoreCase) ||
        _authPath.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    private void FileChanged(object sender, FileSystemEventArgs e)
    { if (AffectsAuth(e.FullPath)) RequestCheck(); }
    private void FileRenamed(object sender, RenamedEventArgs e)
    { if (AffectsAuth(e.FullPath) || AffectsAuth(e.OldFullPath)) RequestCheck(); }
    private void WatcherError(object sender, ErrorEventArgs e)
    {
        lock (_sync) _watcherFailed = true;
        RequestCheck();
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        Task? poller;
        FileSystemWatcher? watcher;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _checks.Writer.TryComplete();
            watcher = _watcher;
            _watcher = null;
            worker = _worker;
            poller = _poller;
        }
        _lifetime.Cancel();
        watcher?.Dispose();
        // Never hold _sync while waiting for callbacks. A callback may itself request disposal.
        if (worker is not null && !ReferenceEquals(CallingMonitor.Value, this)) await worker.ConfigureAwait(false);
        if (poller is not null) await poller.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
