using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTracker.App.Updates;

internal sealed record UpdateManifest(string Target, string StagedExecutable, string OldHash, string NewHash, int ParentId, long ParentStartedUtcTicks, bool RelaunchInBackground = true);

public static class UpdateBootstrap
{
    public static async Task<bool> TryHandleAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--apply-update");
        if (index < 0) return false;
        try
        {
            if (index + 1 >= args.Length) throw new InvalidDataException("Mise à jour incomplète.");
            var manifestPath = ValidateStageFile(args[index + 1], "update.json");
            var stage = Path.GetDirectoryName(manifestPath)!;
            if (new FileInfo(manifestPath).Length > 8192) throw new InvalidDataException("Le manifeste de mise à jour est invalide.");
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(await File.ReadAllTextAsync(manifestPath)) ?? throw new InvalidDataException();
            if (!string.Equals(Path.GetFullPath(manifest.StagedExecutable), Path.Combine(stage, "payload", "CodexTracker.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fichier de mise à jour invalide.");
            using var parent = Process.GetProcessById(manifest.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != manifest.ParentStartedUtcTicks ||
                !string.Equals(Path.GetFullPath(parent.MainModule?.FileName ?? ""), Path.GetFullPath(manifest.Target), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(manifest.Target), "CodexTracker.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Le processus à mettre à jour n’a pas pu être vérifié.");
            UpdatePackage.RejectReparsePoints(manifest.Target);
            await File.WriteAllTextAsync(Path.Combine(stage, "helper.ready"), "ready");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (!parent.HasExited)
            {
                if (File.Exists(Path.Combine(stage, "cancel"))) { await File.WriteAllTextAsync(Path.Combine(stage, "complete"), "cancelled"); return true; }
                await Task.Delay(100, deadline.Token);
            }
            if (File.Exists(Path.Combine(stage, "cancel"))) { await File.WriteAllTextAsync(Path.Combine(stage, "complete"), "cancelled"); return true; }
            var health = Path.Combine(stage, "health.ready");
            await UpdateInstaller.InstallAsync(manifest.Target, manifest.StagedExecutable, manifest.OldHash, manifest.NewHash,
                (target, verifyHealth, token) => LaunchAsync(target, verifyHealth ? health : null, manifest.RelaunchInBackground, token));
            await File.WriteAllTextAsync(Path.Combine(stage, "complete"), "complete");
            await WriteResultAsync("La mise à jour a été installée.", true);
        }
        catch (Exception error)
        {
            await WriteResultAsync(error is OperationCanceledException ? "La mise à jour a été annulée car Codex Tracker n’a pas quitté à temps." : error.Message);
        }
        return true;
    }

    public static void MarkHealthy(string[] args)
    {
        var index = Array.IndexOf(args, "--update-health");
        if (index < 0 || index + 1 >= args.Length) return;
        try { File.WriteAllText(ValidateStageFile(args[index + 1], "health.ready"), "ready"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
    }

    internal static string ValidateStageFile(string path, string expectedName)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        var root = Path.GetFullPath(UpdateService.UpdatesRoot);
        if (!string.Equals(Path.GetFileName(full), expectedName, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
            !string.Equals(Path.GetDirectoryName(directory), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Le dossier de préparation de la mise à jour est invalide.");
        UpdatePackage.RejectReparsePoints(full);
        return full;
    }

    private static async Task<bool> LaunchAsync(string target, string? health, bool background, CancellationToken token)
    {
        if (health is not null && File.Exists(health)) File.Delete(health);
        var info = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(target)! };
        if (background) info.ArgumentList.Add("--background");
        if (health is not null) { info.ArgumentList.Add("--update-health"); info.ArgumentList.Add(health); }
        using var process = Process.Start(info);
        if (process is null) return false;
        if (health is null) return true;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (process.HasExited) return false;
            if (File.Exists(health)) return true;
            await Task.Delay(200, token);
        }
        // Ask only the tracker we just launched to stop; never terminate it forcibly or touch Codex.
        using var exit = Process.Start(new ProcessStartInfo(target, "--exit") { UseShellExecute = false, CreateNoWindow = true });
        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(closeTimeout.Token); }
        catch (OperationCanceledException) { throw new IOException("La nouvelle application reste ouverte. Quittez Codex Tracker avant de restaurer la copie de secours."); }
        return false;
    }

    private static async Task WriteResultAsync(string message, bool success = false)
    {
        try
        {
            Directory.CreateDirectory(UpdateService.UpdatesRoot);
            await File.WriteAllTextAsync(Path.Combine(UpdateService.UpdatesRoot, "last-result.json"),
                JsonSerializer.Serialize(new UpdateOutcome(DateTimeOffset.UtcNow, success, message)));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
