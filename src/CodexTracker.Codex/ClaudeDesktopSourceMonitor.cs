using System.Threading.Channels;

namespace CodexTracker.Codex;

/// <summary>
/// Invalidates passive Desktop readings from native metadata/cache file events. No file content is opened.
/// The ordinary tracker cadence remains the fallback for a dropped filesystem event.
/// </summary>
internal sealed class ClaudeDesktopSourceMonitor : IAsyncDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private static readonly AsyncLocal<ClaudeDesktopSourceMonitor?> CallingMonitor = new();
    private readonly Func<CancellationToken, Task> _onChanged;
    private readonly TimeSpan _pollInterval;
    private readonly Target[] _targets;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
    private Task? _worker;
    private Task? _poller;
    private bool _started, _disposed, _retry;

    private enum Kind { Sessions, AgentConfigurations, Cache }
    private sealed class Target(string path, Kind kind)
    {
        internal string Path { get; } = path;
        internal Kind Kind { get; } = kind;
        internal FileSystemWatcher? Watcher;
        internal bool Failed;
    }

    internal ClaudeDesktopSourceMonitor(IReadOnlyList<string> historyPaths, Func<CancellationToken, Task> onChanged,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        _onChanged = onChanged;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        if (_pollInterval <= TimeSpan.Zero || _pollInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _targets = historyPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(root => new[] { new Target(Path.Combine(root, "claude-code-sessions"), Kind.Sessions),
                new Target(Path.Combine(root, "local-agent-mode-sessions"), Kind.AgentConfigurations),
                new Target(Path.Combine(root, "Cache", "Cache_Data"), Kind.Cache) }).ToArray();
    }

    internal void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            EnsureWatchers();
            var token = _lifetime.Token;
            _worker = Task.Run(() => ObserveAsync(token));
            _poller = Task.Run(() => PollAsync(token));
        }
    }

    private async Task PollAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                // Only repair watcher attachment. Do not enumerate sessions or read Chromium data here.
                lock (_sync)
                    if (!_disposed && (EnsureWatchers() || _retry)) _changes.Writer.TryWrite(true);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task ObserveAsync(CancellationToken token)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                await Task.Delay(Debounce, token).ConfigureAwait(false);
                while (_changes.Reader.TryRead(out _)) { }
                var previous = CallingMonitor.Value;
                try
                {
                    CallingMonitor.Value = this;
                    await _onChanged(token).ConfigureAwait(false);
                    lock (_sync) _retry = false;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // No native path, payload or consumer exception is retained.
                    lock (_sync) _retry = true;
                }
                finally { CallingMonitor.Value = previous; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // Called under _sync. Missing roots are observed from their nearest existing ancestor.
    private bool EnsureWatchers()
    {
        var changed = false;
        foreach (var target in _targets)
        {
            var directory = target.Path;
            while (!Directory.Exists(directory))
            {
                var parent = Path.GetDirectoryName(directory);
                if (parent is null || parent == directory) break;
                directory = parent;
            }
            if (!target.Failed && target.Watcher is { } current && string.Equals(current.Path, directory, StringComparison.OrdinalIgnoreCase)) continue;
            target.Watcher?.Dispose();
            target.Watcher = null;
            target.Failed = false;
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    Filter = "*", IncludeSubdirectories = target.Kind != Kind.Cache || !string.Equals(directory, target.Path, StringComparison.OrdinalIgnoreCase),
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += (_, e) => Changed(target, e.FullPath, structural: false);
                watcher.Created += (_, e) => Changed(target, e.FullPath, structural: true);
                watcher.Deleted += (_, e) => Changed(target, e.FullPath, structural: true);
                watcher.Renamed += (_, e) => { Changed(target, e.OldFullPath, structural: true); Changed(target, e.FullPath, structural: true); };
                watcher.Error += (_, _) =>
                {
                    lock (_sync) target.Failed = true;
                    _changes.Writer.TryWrite(true);
                };
                target.Watcher = watcher;
                watcher.EnableRaisingEvents = true;
                changed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                target.Watcher?.Dispose();
                target.Watcher = null;
            }
        }
        return changed;
    }

    private void Changed(Target target, string path, bool structural)
    {
        if (Affects(target, path, structural)) _changes.Writer.TryWrite(true);
    }

    private static bool Affects(Target target, string path, bool structural)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmed, target.Path, StringComparison.OrdinalIgnoreCase) ||
            target.Path.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return structural;
        if (!path.StartsWith(target.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path[(target.Path.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target.Kind == Kind.Cache)
            return parts.Length == 1 && (parts[0] == "index" || parts[0].StartsWith("data_", StringComparison.Ordinal) || parts[0].StartsWith("f_", StringComparison.Ordinal));
        if (parts.Length < 1 || !Guid.TryParseExact(parts[0], "D", out var account) || account == Guid.Empty) return false;
        if (parts.Length == 1) return structural;
        if (!Guid.TryParseExact(parts[1], "D", out var organization) || organization == Guid.Empty) return false;
        if (parts.Length == 2) return structural;
        var name = target.Kind == Kind.Sessions ? Path.GetFileNameWithoutExtension(parts[2]) : parts[2];
        if (!name.StartsWith("local_", StringComparison.Ordinal) || !Guid.TryParseExact(name[6..], "D", out _)) return false;
        if (target.Kind == Kind.Sessions) return parts.Length == 3 && parts[2].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        // A child of an account/session directory also changes the directory timestamp.
        // Only structural directory events matter; unrelated file writes must not trigger collection.
        return structural && (parts.Length == 3 || parts.Length == 4 && parts[3] == ".claude") ||
            parts.Length == 5 && parts[3] == ".claude" && parts[4] == ".claude.json";
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker, poller;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _changes.Writer.TryComplete();
            foreach (var target in _targets) { target.Watcher?.Dispose(); target.Watcher = null; }
            worker = _worker; poller = _poller;
        }
        _lifetime.Cancel();
        if (worker is not null && !ReferenceEquals(CallingMonitor.Value, this)) await worker.ConfigureAwait(false);
        if (poller is not null) await poller.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
