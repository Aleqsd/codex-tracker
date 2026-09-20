using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexTracker.Codex;

public sealed record DesktopInstallation(string ExecutablePath, string Version);

public interface IDesktopRuntime
{
    DesktopInstallation? FindInstallation();
    bool IsRunning(DesktopInstallation installation);
    Task<bool> CloseAsync(DesktopInstallation installation, CancellationToken cancellationToken);
    Task<bool> LaunchAsync(DesktopInstallation installation, CancellationToken cancellationToken);
}

/// <summary>A version-gated adapter. An auth file write is never considered proof of desktop login.</summary>
public sealed class DesktopSessionManager : IDesktopSessionManager
{
    public const string SupportedDesktopVersion = "26.915.4065.0";
    private readonly IDesktopRuntime runtime;
    private readonly string journalPath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CodexTracker.DesktopSwitch.v1");
    public string AuthFilePath { get; }

    public DesktopSessionManager() : this(
        Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), "auth.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker"),
        new WindowsDesktopRuntime()) { }

    public DesktopSessionManager(string authFilePath, string dataDirectory, IDesktopRuntime runtime)
    {
        AuthFilePath = Path.GetFullPath(authFilePath);
        journalPath = Path.Combine(Path.GetFullPath(dataDirectory), "pending-desktop-switch.bin");
        this.runtime = runtime;
    }

    public string? PendingTargetEmail
    {
        get { try { return ReadJournal()?.TargetEmail; } catch { return "Vérification de session nécessaire"; } }
    }

    public Task<DesktopAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installation = runtime.FindInstallation();
        var running = installation is not null && runtime.IsRunning(installation);
        string? reason = null;
        if (File.Exists(journalPath)) reason = "Confirmez ou restaurez d’abord la bascule précédente.";
        else if (installation is null) reason = "Application Codex Windows introuvable. Ouvrez Codex et réessayez.";
        else if (installation.Version != SupportedDesktopVersion)
            reason = "Cette version de Codex n’a pas été validée pour la bascule. Changez de compte dans Codex.";
        else if (!File.Exists(AuthFilePath)) reason = "La bascule nécessite une session ChatGPT enregistrée en fichier.";
        else
        {
            try { ValidateAuth(File.ReadAllBytes(AuthFilePath), null); }
            catch { reason = "Le format de connexion actif n’est pas compatible avec la bascule."; }
        }
        return Task.FromResult(new DesktopAvailability(reason is null, running, reason, installation?.Version));
    }

    public async Task<DesktopActivationResult> ActivateAsync(byte[] targetAuthJson, string expectedEmail,
        bool confirmed, CancellationToken cancellationToken = default)
    {
        // Validate explicit consent before inspecting or mutating the desktop session.
        if (!confirmed) return new(false, "Changement de compte annulé.");
        await gate.WaitAsync(cancellationToken);
        byte[]? previous = null;
        DesktopInstallation? installation = null;
        var replaced = false;
        var wasRunning = false;
        try
        {
            var availability = await GetAvailabilityAsync(cancellationToken);
            if (!availability.CanSwitch) return new(false, availability.Reason ?? "Bascule indisponible.");
            ValidateAuth(targetAuthJson, expectedEmail);
            installation = runtime.FindInstallation()!;
            wasRunning = runtime.IsRunning(installation);
            if (!await runtime.CloseAsync(installation, cancellationToken))
                return new(false, "Codex n’a pas quitté complètement. Quittez-le depuis son menu, puis réessayez. Aucune session n’a été remplacée.");
            cancellationToken.ThrowIfCancellationRequested();
            // Capture AFTER all desktop children have exited: its final token rotation must not be lost.
            previous = await File.ReadAllBytesAsync(AuthFilePath, cancellationToken);
            ValidateAuth(previous, null);
            WriteJournal(new SwitchJournal(Convert.ToBase64String(previous), expectedEmail,
                installation.ExecutablePath, installation.Version));
            WriteAuthAtomically(targetAuthJson);
            replaced = true;
            // From this point cancellation cannot leave half a transaction behind.
            if (!await runtime.LaunchAsync(installation, CancellationToken.None))
                throw new IOException("Desktop did not start.");
            ValidateAuth(await File.ReadAllBytesAsync(AuthFilePath), expectedEmail);
            return new(false,
                "Codex a été relancé. Vérifiez l’adresse dans son menu de compte, puis confirmez ici.",
                NeedsUserVerification: true, PreviousAuthJson: previous);
        }
        catch (OperationCanceledException) when (!replaced)
        {
            if (wasRunning && installation is not null && !runtime.IsRunning(installation))
                await runtime.LaunchAsync(installation, CancellationToken.None);
            return new(false, "Bascule annulée ; la session précédente est conservée.", PreviousAuthJson: previous);
        }
        catch
        {
            if (replaced && previous is not null && installation is not null)
            {
                try
                {
                    if (!await runtime.CloseAsync(installation, CancellationToken.None))
                        return new(false, "La relance a échoué. Quittez Codex puis utilisez « Restaurer la session précédente ».", true, previous);
                    // Even a failed launch can have rotated the target session. Return its final cache
                    // to the vault owner instead of silently replacing its only current credentials.
                    var displaced = File.Exists(AuthFilePath) ? await File.ReadAllBytesAsync(AuthFilePath) : null;
                    WriteAuthAtomically(previous);
                    var restarted = await runtime.LaunchAsync(installation, CancellationToken.None);
                    File.Delete(journalPath);
                    return new(false, restarted
                        ? "La bascule a échoué. La session précédente a été restaurée et Codex relancé."
                        : "La bascule a échoué. La session précédente a été restaurée ; ouvrez Codex manuellement.", PreviousAuthJson: previous, DisplacedAuthJson: displaced);
                }
                catch { return new(false, "La restauration automatique a échoué. La sauvegarde chiffrée est conservée ; réessayez la restauration.", true, previous); }
            }
            if (wasRunning && installation is not null && !runtime.IsRunning(installation))
                await runtime.LaunchAsync(installation, CancellationToken.None);
            return new(false, "La bascule n’a pas pu être préparée. La session précédente est conservée.", PreviousAuthJson: previous);
        }
        finally { gate.Release(); }
    }

    public async Task<DesktopActivationResult> ConfirmActivationAsync(bool accepted, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var journal = ReadJournal();
            if (journal is null) return new(false, "Aucune bascule à vérifier.");
            var installation = runtime.FindInstallation();
            if (installation is null || installation.Version != journal.DesktopVersion ||
                !string.Equals(installation.ExecutablePath, journal.DesktopPath, StringComparison.OrdinalIgnoreCase))
                return new(false, "Codex a changé depuis la bascule. La sauvegarde est conservée ; ouvrez le compte manuellement.", true);
            if (accepted)
            {
                if (!runtime.IsRunning(installation))
                    return new(false, "Ouvrez Codex et vérifiez le compte avant de confirmer.", true);
                ValidateAuth(await File.ReadAllBytesAsync(AuthFilePath, cancellationToken), journal.TargetEmail);
                File.Delete(journalPath);
                return new(true, "Compte vérifié dans Codex.");
            }
            if (!await runtime.CloseAsync(installation, cancellationToken))
                return new(false, "Quittez complètement Codex pour restaurer la session précédente.", true);
            var latestTarget = File.Exists(AuthFilePath) ? await File.ReadAllBytesAsync(AuthFilePath) : null;
            var backup = Convert.FromBase64String(journal.PreviousAuth);
            ValidateAuth(backup, null);
            WriteAuthAtomically(backup);
            var launched = await runtime.LaunchAsync(installation, CancellationToken.None);
            File.Delete(journalPath);
            return new(true, launched ? "Session précédente restaurée." : "Session précédente restaurée ; ouvrez Codex manuellement.", PreviousAuthJson: latestTarget);
        }
        catch (OperationCanceledException) { return new(false, "Vérification annulée. La sauvegarde reste disponible.", true); }
        catch { return new(false, "Impossible de vérifier ou restaurer la session. La sauvegarde chiffrée reste disponible.", true); }
        finally { gate.Release(); }
    }

    private sealed record SwitchJournal(string PreviousAuth, string TargetEmail, string DesktopPath, string DesktopVersion);

    private SwitchJournal? ReadJournal()
    {
        if (!File.Exists(journalPath)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(journalPath), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<SwitchJournal>(plain) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void WriteJournal(SwitchJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(journal);
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var temp = journalPath + ".tmp";
            File.WriteAllBytes(temp, cipher);
            RestrictFile(temp);
            File.Move(temp, journalPath, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void WriteAuthAtomically(byte[] bytes)
    {
        var temporary = AuthFilePath + ".tracker-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            RestrictFile(temporary);
            File.Move(temporary, AuthFilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RestrictFile(string path)
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(sid);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    private static void ValidateAuth(byte[] bytes, string? expectedEmail)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("auth_mode", out var mode) || mode.GetString() != "chatgpt" ||
            !root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Unsupported credential format.");
        foreach (var name in new[] { "access_token", "refresh_token", "id_token", "account_id" })
            if (!tokens.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("Incomplete session.");
        if (expectedEmail is null) return;
        var parts = tokens.GetProperty("id_token").GetString()!.Split('.');
        if (parts.Length != 3) throw new InvalidDataException();
        var encoded = parts[1].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(encoded));
        var email = claims.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
        if (!string.Equals(email, expectedEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Account identity mismatch.");
    }
}

public sealed class WindowsDesktopRuntime : IDesktopRuntime
{
    private static readonly Regex PackagePattern = new(@"[\\/]OpenAI\.Codex_(?<version>\d+\.\d+\.\d+\.\d+)_x64__2p2nqsd0c76g0[\\/]app[\\/](ChatGPT|Codex)\.exe$", RegexOptions.IgnoreCase);

    public DesktopInstallation? FindInstallation()
    {
        foreach (var p in DesktopProcesses())
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    var match = path is null ? Match.Empty : PackagePattern.Match(path);
                    if (match.Success) return new(path!, match.Groups["version"].Value);
                }
                catch { }
            }
        }
        // The package is not running: inspect only its registered package locations, never an arbitrary EXE.
        try
        {
            using var applications = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (applications is null) return null;
            foreach (var name in applications.GetSubKeyNames().Where(n => n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n))
            {
                using var entry = applications.OpenSubKey(name);
                var root = entry?.GetValue("PackageRootFolder") as string;
                if (root is null) continue;
                foreach (var binary in new[] { "ChatGPT.exe", "Codex.exe" })
                {
                    var candidate = Path.Combine(root, "app", binary);
                    var match = PackagePattern.Match(candidate);
                    if (match.Success && File.Exists(candidate)) return new(candidate, match.Groups["version"].Value);
                }
            }
        }
        catch { }
        return null;
    }

    public bool IsRunning(DesktopInstallation installation) => GetPackageProcessIds(installation).Count != 0;

    public async Task<bool> CloseAsync(DesktopInstallation installation, CancellationToken cancellationToken)
    {
        var ids = GetPackageProcessIds(installation);
        if (ids.Count == 0) return true;
        var all = WithDescendants(ids);
        foreach (var id in ids)
        {
            try { using var process = Process.GetProcessById(id); if (process.MainWindowHandle != 0) process.CloseMainWindow(); }
            catch (ArgumentException) { }
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Include newly spawned package processes as well as the original core/children.
            if (!all.Any(IsAlive) && !IsRunning(installation)) return true;
            await Task.Delay(250, cancellationToken);
        }
        return false;
    }

    public async Task<bool> LaunchAsync(DesktopInstallation installation, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(installation.ExecutablePath) || !PackagePattern.IsMatch(installation.ExecutablePath)) return false;
            using var started = Process.Start(new ProcessStartInfo(installation.ExecutablePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installation.ExecutablePath)!,
                WindowStyle = ProcessWindowStyle.Normal // Explicitly requested interactive Codex window.
            });
            for (var i = 0; i < 60; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsRunning(installation)) { await Task.Delay(1000, cancellationToken); return IsRunning(installation); }
                await Task.Delay(250, cancellationToken);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return false;
    }

    private static IEnumerable<Process> DesktopProcesses() => Process.GetProcessesByName("ChatGPT").Concat(Process.GetProcessesByName("Codex"));
    private static HashSet<int> GetPackageProcessIds(DesktopInstallation installation)
    {
        var ids = new HashSet<int>();
        foreach (var p in DesktopProcesses())
        {
            using (p)
            {
                try { if (string.Equals(p.MainModule?.FileName, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)) ids.Add(p.Id); }
                catch { }
            }
        }
        return ids;
    }

    private static bool IsAlive(int id)
    {
        try { using var p = Process.GetProcessById(id); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch { return true; }
    }

    private static HashSet<int> WithDescendants(HashSet<int> roots)
    {
        var result = new HashSet<int>(roots);
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entries = new List<(int Id, int Parent)>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry))
                do { entries.Add(((int)entry.ProcessId, (int)entry.ParentProcessId)); } while (Process32Next(snapshot, ref entry));
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (id, parent) in entries)
                    if (result.Contains(parent) && result.Add(id)) changed = true;
            }
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
