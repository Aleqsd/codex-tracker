using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTracker.App.Updates;

public sealed record PreparedUpdate(UpdateRelease Release, string StageId, string ExecutableHash,
    DateTimeOffset PreparedAt, bool AutomaticAttempted = false);

public sealed partial class UpdateService
{
    private readonly SemaphoreSlim _preparationGate = new(1, 1);
    private string PreparationRoot => _cachePath is null ? UpdatesRoot : Path.GetDirectoryName(_cachePath)!;
    private string PreparedPathFor(bool previews) => Path.Combine(PreparationRoot, previews ? "prepared-update-preview.json" : "prepared-update-stable.json");
    public PreparedUpdate? Prepared { get; private set; }
    public bool IsPreparing { get; private set; }
    public string? PreparationMessage { get; private set; }
    public event EventHandler? PreparationChanged;

    // Called once at startup, before deciding whether to apply an already downloaded update.
    public async Task<PreparedUpdate?> LoadPreparedAsync(CancellationToken token = default)
    {
        await _preparationGate.WaitAsync(token);
        try { return await LoadPreparedCoreAsync(token); }
        finally { _preparationGate.Release(); }
    }

    private async Task<PreparedUpdate?> LoadPreparedCoreAsync(CancellationToken token)
    {
        var channelRevision = _channelRevision;
        var previews = IncludePrereleases;
        var preparedPath = PreparedPathFor(previews);
        Prepared = null;
        PreparationMessage = null;
        try
        {
            UpdatePackage.RejectReparsePoints(preparedPath);
            if (!File.Exists(preparedPath)) return null;
            if (new FileInfo(preparedPath).Length > 16384) throw new InvalidDataException();
            var pending = JsonSerializer.Deserialize<PreparedUpdate>(await File.ReadAllTextAsync(preparedPath, token));
            if (pending is null || pending.Release is null || !IsCachedReleaseValid(pending.Release) ||
                !IsReleaseAllowed(pending.Release, previews) ||
                !Guid.TryParseExact(pending.StageId, "N", out _) || pending.ExecutableHash is not { Length: 64 })
                throw new InvalidDataException();
            if (SemanticVersion.Parse(pending.Release.Version)!.CompareTo(SemanticVersion.Parse(CurrentVersion)) <= 0)
            { File.Delete(preparedPath); MarkAbandoned(Path.Combine(PreparationRoot, pending.StageId)); return null; }
            var executable = PreparedExecutable(pending);
            UpdatePackage.RejectReparsePoints(executable);
            if (!string.Equals(await UpdatePackage.HashAsync(executable, token), pending.ExecutableHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            lock (_checkGate)
            {
                if (channelRevision != _channelRevision) return null;
                Prepared = pending;
                PreparationMessage = pending.AutomaticAttempted
                    ? $"Version {pending.Release.Version} prête. L’installation automatique a déjà été tentée ; vous pouvez réessayer."
                    : $"Version {pending.Release.Version} téléchargée et vérifiée.";
            }
            return pending;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            if (channelRevision == _channelRevision)
                PreparationMessage = "La mise à jour préparée est absente ou invalide. Relancez sa recherche pour la télécharger à nouveau.";
            return null;
        }
        finally { PreparationChanged?.Invoke(this, EventArgs.Empty); }
    }

    internal string PreparedExecutable(PreparedUpdate pending) => Path.Combine(PreparationRoot, pending.StageId, "payload", "CodexTracker.exe");

    public async Task<PreparedUpdate> PrepareAsync(UpdateRelease release, CancellationToken token = default)
    {
        if (!IsCachedReleaseValid(release) || !IsReleaseAllowed(release, IncludePrereleases) || SemanticVersion.Parse(release.Version)!.CompareTo(SemanticVersion.Parse(CurrentVersion)) <= 0)
            throw new InvalidDataException("La source de mise à jour n’est pas autorisée.");
        await _preparationGate.WaitAsync(token);
        var channelRevision = _channelRevision;
        var previews = IncludePrereleases;
        string? stage = null;
        try
        {
            if (!IsReleaseAllowed(release, previews)) throw new OperationCanceledException("Le canal de mise à jour a changé.");
            var previous = await LoadPreparedCoreAsync(token);
            EnsureChannel(channelRevision);
            if (previous is not null && SemanticVersion.Parse(previous.Release.Version)!.CompareTo(SemanticVersion.Parse(release.Version)) >= 0)
                return previous;
            IsPreparing = true;
            PreparationMessage = $"Téléchargement de la version {release.Version}…";
            PreparationChanged?.Invoke(this, EventArgs.Empty);
            UpdatePackage.RejectReparsePoints(PreparationRoot);
            var id = Guid.NewGuid().ToString("N");
            stage = Path.Combine(PreparationRoot, id);
            Directory.CreateDirectory(stage);
            MarkAbandoned(stage); // Allows cleanup of a download interrupted by a process crash.
            var executable = await StagePackageAsync(release, stage, token).ConfigureAwait(false);
            var prepared = new PreparedUpdate(release, id, await UpdatePackage.HashAsync(executable, token), _clock());
            EnsureChannel(channelRevision);
            await SavePreparedAsync(prepared, previews, token);
            lock (_checkGate)
            {
                EnsureChannel(channelRevision);
                Prepared = prepared;
                PreparationMessage = $"Version {release.Version} téléchargée et vérifiée.";
            }
            try { File.Delete(Path.Combine(stage, "abandoned")); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (previous is not null) MarkAbandoned(Path.Combine(PreparationRoot, previous.StageId));
            return prepared;
        }
        catch
        {
            if (stage is not null) MarkAbandoned(stage);
            if (channelRevision == _channelRevision)
                PreparationMessage = "Téléchargement interrompu. Le suivi continue ; une nouvelle tentative sera effectuée plus tard.";
            throw;
        }
        finally { IsPreparing = false; _preparationGate.Release(); PreparationChanged?.Invoke(this, EventArgs.Empty); }
    }

    // Persist before launching the helper: a crash or rollback must never cause a startup loop.
    internal async Task<PreparedUpdate?> ClaimPreparedAsync(bool automatic, CancellationToken token)
    {
        await _preparationGate.WaitAsync(token);
        var channelRevision = _channelRevision;
        var previews = IncludePrereleases;
        try
        {
            var pending = await LoadPreparedCoreAsync(token);
            if (pending is null || automatic && pending.AutomaticAttempted) return null;
            var claimed = pending with { AutomaticAttempted = true };
            if (channelRevision != _channelRevision) return null;
            await SavePreparedAsync(claimed, previews, token);
            lock (_checkGate)
            {
                if (channelRevision != _channelRevision) return null;
                Prepared = claimed;
            }
            return claimed;
        }
        finally { _preparationGate.Release(); }
    }

    private void EnsureChannel(int revision)
    {
        if (revision != _channelRevision) throw new OperationCanceledException("Le canal de mise à jour a changé.");
    }

    private async Task SavePreparedAsync(PreparedUpdate pending, bool previews, CancellationToken token)
    {
        var preparedPath = PreparedPathFor(previews);
        UpdatePackage.RejectReparsePoints(preparedPath);
        Directory.CreateDirectory(PreparationRoot);
        var temporary = preparedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, pending, cancellationToken: token);
                file.Flush(true);
            }
            File.Move(temporary, preparedPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void MarkAbandoned(string stage)
    {
        try { File.WriteAllText(Path.Combine(stage, "abandoned"), "abandoned"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
