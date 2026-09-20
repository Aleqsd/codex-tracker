using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed record StoredSettings(List<AccountProfile> Accounts, Guid? SelectedAccountId,
    bool OnboardingComplete, List<Guid>? DetectedAccountIds = null);

// Only display metadata is persisted. Legacy credential vaults are never opened or modified.
internal sealed class ProfileStore(string root) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _root = Path.GetFullPath(root);
    private FileStream? _lease;
    private string Runtime => Path.Combine(_root, "observer-runtime");

    public void Open()
    {
        SecureDirectory(_root);
        try { _lease = new FileStream(Path.Combine(_root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new TrackerException("Codex Tracker est déjà ouvert. Fermez l'autre instance avant de continuer."); }
        SecureDirectory(Runtime);
        foreach (var directory in Directory.EnumerateDirectories(Runtime))
        {
            try { DeleteRuntime(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        return new(emails.Where(email => !string.IsNullOrWhiteSpace(email) && IsEmail(email)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(email => new AccountProfile(Guid.NewGuid(), email.Trim())).ToList(), null, false);
    }

    public Dictionary<Guid, AccountSnapshot> LoadSnapshots()
    {
        var path = Path.Combine(_root, "snapshots.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<Guid, AccountSnapshot>>(File.ReadAllText(path), Json) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    public void SaveSettings(StoredSettings settings) => AtomicWrite(Path.Combine(_root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings, Json));
    public void SaveSnapshots(Dictionary<Guid, AccountSnapshot> snapshots) => AtomicWrite(Path.Combine(_root, "snapshots.json"), JsonSerializer.SerializeToUtf8Bytes(snapshots, Json));

    public string CreateRuntime()
    {
        var directory = Path.Combine(Runtime, Guid.NewGuid().ToString("D"));
        SecureDirectory(directory);
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
