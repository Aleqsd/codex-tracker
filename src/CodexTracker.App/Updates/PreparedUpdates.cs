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
    private string PreparedPath => Path.Combine(PreparationRoot, "prepared-update.json");
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
        Prepared = null;
        PreparationMessage = null;
        try
        {
            UpdatePackage.RejectReparsePoints(PreparedPath);
            if (!File.Exists(PreparedPath)) return null;
            if (new FileInfo(PreparedPath).Length > 16384) throw new InvalidDataException();
            var pending = JsonSerializer.Deserialize<PreparedUpdate>(await File.ReadAllTextAsync(PreparedPath, token));
            if (pending is null || pending.Release is null || !IsCachedReleaseValid(pending.Release) ||
                !Guid.TryParseExact(pending.StageId, "N", out _) || pending.ExecutableHash is not { Length: 64 })
                throw new InvalidDataException();
            if (SemanticVersion.Parse(pending.Release.Version)!.CompareTo(SemanticVersion.Parse(CurrentVersion)) <= 0)
            { File.Delete(PreparedPath); MarkAbandoned(Path.Combine(PreparationRoot, pending.StageId)); return null; }
            var executable = PreparedExecutable(pending);
            UpdatePackage.RejectReparsePoints(executable);
            if (!string.Equals(await UpdatePackage.HashAsync(executable, token), pending.ExecutableHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            Prepared = pending;
            PreparationMessage = pending.AutomaticAttempted
                ? $"Version {pending.Release.Version} prête. L’installation automatique a déjà été tentée ; vous pouvez réessayer."
                : $"Version {pending.Release.Version} téléchargée et vérifiée.";
            return pending;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            PreparationMessage = "La mise à jour préparée est absente ou invalide. Relancez sa recherche pour la télécharger à nouveau.";
            return null;
        }
        finally { PreparationChanged?.Invoke(this, EventArgs.Empty); }
    }

    internal string PreparedExecutable(PreparedUpdate pending) => Path.Combine(PreparationRoot, pending.StageId, "payload", "CodexTracker.exe");

    public async Task<PreparedUpdate> PrepareAsync(UpdateRelease release, CancellationToken token = default)
    {
        if (!IsCachedReleaseValid(release) || SemanticVersion.Parse(release.Version)!.CompareTo(SemanticVersion.Parse(CurrentVersion)) <= 0)
            throw new InvalidDataException("La source de mise à jour n’est pas autorisée.");
        await _preparationGate.WaitAsync(token);
        string? stage = null;
        try
        {
            var previous = await LoadPreparedCoreAsync(token);
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
            await SavePreparedAsync(prepared, token);
            Prepared = prepared;
            PreparationMessage = $"Version {release.Version} téléchargée et vérifiée.";
            try { File.Delete(Path.Combine(stage, "abandoned")); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (previous is not null) MarkAbandoned(Path.Combine(PreparationRoot, previous.StageId));
            return prepared;
        }
        catch
        {
            if (stage is not null) MarkAbandoned(stage);
            PreparationMessage = "Téléchargement interrompu. Le suivi continue ; une nouvelle tentative sera effectuée plus tard.";
            throw;
        }
        finally { IsPreparing = false; _preparationGate.Release(); PreparationChanged?.Invoke(this, EventArgs.Empty); }
    }

    // Persist before launching the helper: a crash or rollback must never cause a startup loop.
    internal async Task<PreparedUpdate?> ClaimPreparedAsync(bool automatic, CancellationToken token)
    {
        await _preparationGate.WaitAsync(token);
        try
        {
            var pending = await LoadPreparedCoreAsync(token);
            if (pending is null || automatic && pending.AutomaticAttempted) return null;
            var claimed = pending with { AutomaticAttempted = true };
            await SavePreparedAsync(claimed, token);
            Prepared = claimed;
            return claimed;
        }
        finally { _preparationGate.Release(); }
    }

    private async Task SavePreparedAsync(PreparedUpdate pending, CancellationToken token)
    {
        UpdatePackage.RejectReparsePoints(PreparedPath);
        Directory.CreateDirectory(PreparationRoot);
        var temporary = PreparedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, pending, cancellationToken: token);
                file.Flush(true);
            }
            File.Move(temporary, PreparedPath, true);
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
