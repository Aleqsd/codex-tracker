using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed record StoredSettings(List<AccountProfile> Accounts, Guid? SelectedAccountId, bool OnboardingComplete);

internal sealed class ProfileStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _root;
    private FileStream? _lease;
    private string Vault => Path.Combine(_root, "vault");
    private string Runtime => Path.Combine(_root, "runtime");

    public ProfileStore(string root) { _root = Path.GetFullPath(root); }

    public void Open()
    {
        SecureDirectory(_root);
        try { _lease = new FileStream(Path.Combine(_root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new TrackerException("Codex Tracker est déjà ouvert. Fermez l'autre instance avant de continuer."); }
        SecureDirectory(Vault);
        SecureDirectory(Runtime);
        var knownAccounts = LoadSettings().Accounts;
        // Recover interrupted managed refreshes before deleting temporary plain-text credentials.
        foreach (var directory in Directory.EnumerateDirectories(Runtime))
        {
            var preserveForRetry = false;
            try
            {
                if (Guid.TryParse(Path.GetFileName(directory), out var id) && HasAuth(id) &&
                    knownAccounts.FirstOrDefault(a => a.Id == id) is { } profile && File.Exists(Path.Combine(directory, "auth.json")))
                {
                    var auth = File.ReadAllBytes(Path.Combine(directory, "auth.json"));
                    try
                    {
                        if (string.Equals(AuthDocument.Parse(auth).Email, profile.Email, StringComparison.OrdinalIgnoreCase))
                        {
                            preserveForRetry = true;
                            SaveAuth(id, auth);
                            preserveForRetry = false;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(auth); }
                }
                DeleteRuntime(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TrackerException or JsonException)
            {
                if (preserveForRetry)
                    throw new TrackerException("La session récupérée ne peut pas être chiffrée ou enregistrée. Libérez de l'espace puis redémarrez le tracker ; ses données temporaires sont conservées.");
                try { DeleteRuntime(directory); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public StoredSettings LoadSettings()
    {
        var path = Path.Combine(_root, "settings.json");
        if (File.Exists(path))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path), Json) ?? throw new JsonException();
                if (settings.Accounts is null || settings.Accounts.Any(a => a is null || string.IsNullOrWhiteSpace(a.Email) || !IsEmail(a.Email)) ||
                    settings.Accounts.Select(a => a.Id).Distinct().Count() != settings.Accounts.Count ||
                    settings.Accounts.Select(a => a.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Accounts.Count)
                    throw new JsonException();
                return settings;
            }
            catch (JsonException) { throw new TrackerException("Les réglages locaux sont illisibles. Conservez settings.json avant de les réinitialiser."); }
        }
        var seed = Path.Combine(_root, "initial-accounts.json");
        var emails = File.Exists(seed) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(seed), Json) ?? [] : [];
        return new(emails.Where(email => !string.IsNullOrWhiteSpace(email) && IsEmail(email)).Distinct(StringComparer.OrdinalIgnoreCase).Select(email => new AccountProfile(Guid.NewGuid(), email.Trim())).ToList(), null, false);
    }

    public Dictionary<Guid, AccountSnapshot> LoadSnapshots()
    {
        var path = Path.Combine(_root, "snapshots.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<Guid, AccountSnapshot>>(File.ReadAllText(path), Json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    public void SaveSettings(StoredSettings settings) => AtomicWrite(Path.Combine(_root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings, Json));
    public void SaveSnapshots(Dictionary<Guid, AccountSnapshot> snapshots) => AtomicWrite(Path.Combine(_root, "snapshots.json"), JsonSerializer.SerializeToUtf8Bytes(snapshots, Json));
    public bool HasAuth(Guid id) => File.Exists(Path.Combine(Vault, $"{id:D}.bin"));
    public byte[] LoadAuth(Guid id)
    {
        try { return ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Vault, $"{id:D}.bin")), id.ToByteArray(), DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new TrackerException("La session chiffrée ne peut plus être déverrouillée. Reconnectez ce compte."); }
    }
    public void SaveAuth(Guid id, byte[] auth) => AtomicWrite(Path.Combine(Vault, $"{id:D}.bin"),
        ProtectedData.Protect(auth, id.ToByteArray(), DataProtectionScope.CurrentUser));
    public void DeleteAuth(Guid id) => File.Delete(Path.Combine(Vault, $"{id:D}.bin"));

    public string CreateRuntime(Guid id, byte[]? auth)
    {
        var directory = Path.Combine(Runtime, id.ToString("D"));
        if (File.Exists(Path.Combine(directory, "auth.json")))
            throw new TrackerException("Une session temporaire attend encore sa sauvegarde. Redémarrez Codex Tracker pour la récupérer.");
        SecureDirectory(directory);
        if (auth is not null) AtomicWrite(Path.Combine(directory, "auth.json"), auth);
        // No user config, hooks, MCP servers or project state are inherited by the isolated collector.
        return directory;
    }

    public void DeleteRuntime(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(Runtime) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid runtime directory.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }

    public static bool IsEmail(string email) => System.Net.Mail.MailAddress.TryCreate(email.Trim(), out var address) &&
        string.Equals(address.Address, email.Trim(), StringComparison.OrdinalIgnoreCase) && address.Host.Contains('.');

    private static void SecureDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new TrackerException("L'identité Windows est indisponible.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void AtomicWrite(string destination, byte[] data)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(data); file.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() => _lease?.Dispose();
}
