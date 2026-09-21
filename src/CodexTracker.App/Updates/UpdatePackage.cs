using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CodexTracker.App.Updates;

internal static class UpdatePackage
{
    internal const long MaximumArchiveBytes = 512L * 1024 * 1024;
    internal const long MaximumExpandedBytes = 1024L * 1024 * 1024;

    internal static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }

    internal static async Task VerifyAsync(string archive, string checksum, string assetName, CancellationToken token = default)
    {
        var match = Regex.Match(checksum.Trim(), @"\A([0-9a-fA-F]{64})[ \t]+\*?([^\r\n]+)\z");
        if (!match.Success || !string.Equals(match.Groups[2].Value, assetName, StringComparison.Ordinal))
            throw new InvalidDataException("Le fichier de vérification de la mise à jour est invalide.");
        if (!string.Equals(await HashAsync(archive, token), match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("La vérification SHA-256 a échoué. La mise à jour n’a pas été installée.");
    }

    internal static async Task<string> ExtractExecutableAsync(string archive, string destination, CancellationToken token = default)
    {
        RejectReparsePoints(destination);
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 1024) throw new InvalidDataException("L’archive contient trop de fichiers.");
        long expanded = 0;
        ZipArchiveEntry? executable = null;
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains(':') ||
                name.Split('/').Any(p => p == ".." || p == "." || p.TrimEnd(' ', '.') != p) ||
                !names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("L’archive de mise à jour contient un chemin non autorisé.");
            var full = Path.GetFullPath(Path.Combine(destination, name));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Un fichier de mise à jour sortirait du dossier temporaire.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes) throw new InvalidDataException("L’archive de mise à jour est trop volumineuse.");
            if (string.Equals(name, "CodexTracker.exe", StringComparison.Ordinal)) executable = entry;
        }
        if (executable is null || executable.Length is < 2 or > MaximumArchiveBytes)
            throw new InvalidDataException("L’archive ne contient pas l’application Windows attendue.");
        var output = Path.Combine(destination, "CodexTracker.exe");
        await using (var source = executable.Open())
        await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await UpdateService.CopyBoundedAsync(source, target, executable.Length, token);
            if (target.Length != executable.Length) throw new InvalidDataException("Le téléchargement est incomplet.");
        }
        using (var image = File.OpenRead(output))
            if (image.ReadByte() != 'M' || image.ReadByte() != 'Z')
                throw new InvalidDataException("Le fichier téléchargé n’est pas une application Windows.");
        return output;
    }

    internal static void RejectReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Un lien de dossier empêche la mise à jour automatique. Utilisez l’installateur.");
            current = Path.GetDirectoryName(current);
        }
    }
}

internal static class UpdateInstaller
{
    internal static async Task InstallAsync(string target, string staged, string expectedOldHash, string expectedNewHash,
        Func<string, bool, CancellationToken, Task<bool>> launch, CancellationToken token = default)
    {
        target = Path.GetFullPath(target); staged = Path.GetFullPath(staged);
        if (!string.Equals(Path.GetFileName(target), "CodexTracker.exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, staged, StringComparison.OrdinalIgnoreCase)) throw new IOException("Chemin d’installation invalide.");
        UpdatePackage.RejectReparsePoints(target);
        if (!string.Equals(await UpdatePackage.HashAsync(target, token), expectedOldHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("L’application a changé depuis la préparation. Réessayez la mise à jour.");
        var suffix = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".CodexTracker-update-" + suffix + ".tmp");
        var backup = Path.Combine(Path.GetDirectoryName(target)!, ".CodexTracker-previous-" + suffix + ".exe");
        var replaced = false;
        try
        {
            UpdatePackage.RejectReparsePoints(staged);
            if (!string.Equals(await UpdatePackage.HashAsync(staged, token), expectedNewHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Le fichier téléchargé a changé depuis la préparation.");
            File.Copy(staged, temporary, false);
            token.ThrowIfCancellationRequested();
            await WaitUntilReleasedAsync(target, TimeSpan.FromSeconds(10), token);
            File.Replace(temporary, target, backup, true);
            replaced = true;
            // Once replacement has begun, complete the transaction even if a UI token was cancelled.
            if (!await launch(target, true, CancellationToken.None)) throw new IOException("La nouvelle version n’a pas démarré correctement.");
            // A cleanup failure must not roll back an already healthy, running version.
            try { File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception updateError) when (replaced)
        {
            try
            {
                File.Replace(backup, target, null, true);
            }
            catch (Exception rollbackError)
            {
                var message = File.Exists(backup)
                    ? $"La restauration a échoué. La copie de secours est conservée ici : {backup}"
                    : $"La restauration automatique a échoué. Vérifiez l’application ici : {target}";
                throw new IOException(message,
                    new AggregateException(updateError, rollbackError));
            }
            // File.Replace consumed the backup: from here the restored file is the target itself.
            var relaunched = await TryLaunchAsync(target, launch);
            throw new IOException(relaunched
                ? "La mise à jour a échoué ; la version précédente a été restaurée et relancée."
                : $"La version précédente a été restaurée. Ouvrez-la manuellement ici : {target}", updateError);
        }
        catch (Exception updateError) when (!replaced)
        {
            var relaunched = false;
            try
            {
                UpdatePackage.RejectReparsePoints(target);
                if (string.Equals(await UpdatePackage.HashAsync(target, CancellationToken.None), expectedOldHash, StringComparison.OrdinalIgnoreCase))
                    relaunched = await TryLaunchAsync(target, launch);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw new IOException(relaunched
                ? "La mise à jour n’a pas pu être installée ; la version précédente a été relancée."
                : $"La mise à jour n’a pas pu être installée. Ouvrez Codex Tracker manuellement ici : {target}", updateError);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> TryLaunchAsync(string target, Func<string, bool, CancellationToken, Task<bool>> launch)
    {
        try { return await launch(target, false, CancellationToken.None); }
        catch (Exception) { return false; }
    }

    internal static async Task WaitUntilReleasedAsync(string target, TimeSpan timeout, CancellationToken token)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { using var probe = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return; }
            catch (IOException) when (deadline.Elapsed < timeout) { await Task.Delay(100, token); }
            catch (IOException) { throw new IOException("L’application reste verrouillée, éventuellement par un client MCP. Reconnectez ce client puis réessayez ; aucun fichier n’a été remplacé."); }
        }
    }
}
