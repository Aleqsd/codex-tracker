using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed record StoredSettings(List<AccountProfile> Accounts, Guid? SelectedAccountId,
    bool OnboardingComplete, List<Guid>? DetectedAccountIds = null);
internal sealed record AccountTelemetry(IReadOnlyList<UsageSample> Samples, QuotaAlertState Alerts, Guid? AccountId = null)
{
    public static AccountTelemetry Empty { get; } = new(Array.Empty<UsageSample>(), new());
}

// Only display metadata is persisted. Legacy credential vaults are never opened or modified.
internal sealed partial class ProfileStore(string root) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly string _root = Path.GetFullPath(root);
    private readonly List<string> _recoveryWarnings = [];
    public IReadOnlyList<string> RecoveryWarnings => _recoveryWarnings.AsReadOnly();
    private FileStream? _lease;
    private string Runtime => Path.Combine(_root, "observer-runtime");
    private string Usage => Path.Combine(_root, "usage");

    public void Open()
    {
        SecureDirectory(_root);
        try { _lease = new FileStream(Path.Combine(_root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new TrackerException("Codex Tracker est déjà ouvert. Fermez l'autre instance avant de continuer."); }
        SecureDirectory(Runtime);
        SecureDirectory(Usage);
        foreach (var directory in Directory.EnumerateDirectories(Runtime))
        {
            try { DeleteRuntime(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public StoredSettings LoadSettings()
    {
        var path = Path.Combine(_root, "settings.json");
        var loaded = RecoverableJsonFile.Read(path, ParseSettings);
        if (loaded.Value is { } settings)
        {
            if (loaded.RecoveryRequired) Warn("Les comptes ont été récupérés depuis la dernière sauvegarde locale valide. Le fichier d’origine est conservé.");
            return settings;
        }
        if (loaded.RecoveryRequired) throw new TrackerException("La liste des comptes et sa sauvegarde sont illisibles. Vos fichiers sont conservés ; aucune réinitialisation automatique n’a été effectuée. Restaurez une sauvegarde locale valide avant de relancer le tracker.");
        var seed = Path.Combine(_root, "initial-accounts.json");
        var emails = File.Exists(seed) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(seed), Json) ?? [] : [];
        return new(emails.Where(email => !string.IsNullOrWhiteSpace(email) && IsEmail(email)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(email => new AccountProfile(Guid.NewGuid(), email.Trim())).ToList(), null, false);
    }

    public Dictionary<Guid, AccountSnapshot> LoadSnapshots()
    {
        var path = Path.Combine(_root, "snapshots.json");
        var loaded = RecoverableJsonFile.Read(path, ParseSnapshots);
        if (loaded.RecoveryRequired) Warn(loaded.UsedBackup
            ? "Les derniers relevés ont été récupérés depuis la sauvegarde locale. Leurs dates d’observation restent inchangées."
            : "Les relevés locaux sont illisibles. Ils restent conservés ; seul le compte ouvert dans Codex peut être actualisé.");
        return loaded.Value ?? [];
    }

    public void SaveSettings(StoredSettings settings) => RecoverableJsonFile.Write(Path.Combine(_root, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(settings, Json), ParseSettings);
    public void SaveSnapshots(Dictionary<Guid, AccountSnapshot> snapshots) => RecoverableJsonFile.Write(Path.Combine(_root, "snapshots.json"), JsonSerializer.SerializeToUtf8Bytes(snapshots, Json), ParseSnapshots);

    private void Warn(string message) { if (!_recoveryWarnings.Contains(message)) _recoveryWarnings.Add(message); }

    private static StoredSettings ParseSettings(byte[] bytes)
    {
        var settings = JsonSerializer.Deserialize<StoredSettings>(bytes, Json) ?? throw new JsonException();
        if (settings.Accounts is null || settings.Accounts.Any(a => a is null || a.Id == Guid.Empty || string.IsNullOrWhiteSpace(a.Email) || !IsEmail(a.Email)) ||
            settings.Accounts.Select(a => a.Id).Distinct().Count() != settings.Accounts.Count ||
            settings.Accounts.Select(a => a.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Accounts.Count)
            throw new JsonException();
        return settings;
    }

    private static Dictionary<Guid, AccountSnapshot> ParseSnapshots(byte[] bytes)
    {
        var values = JsonSerializer.Deserialize<Dictionary<Guid, AccountSnapshot>>(bytes, Json) ?? throw new JsonException();
        if (values.Any(pair => pair.Key == Guid.Empty || pair.Value is not { Email: { Length: > 0 }, Buckets: not null } snapshot ||
            !IsEmail(snapshot.Email) || snapshot.Buckets.Any(bucket => bucket is not { Id: not null, Windows: not null } ||
                bucket.Windows.Any(window => window is null || !double.IsFinite(window.UsedPercent))) ||
            snapshot.ResetCredits?.Any(credit => credit is not { Id: not null }) == true)) throw new JsonException();
        return values;
    }

    public AccountTelemetry LoadTelemetry(Guid accountId, DateTimeOffset now)
    {
        var path = Path.Combine(Usage, $"{accountId:D}.json");
        if (!File.Exists(path)) return AccountTelemetry.Empty;
        try
        {
            if (new FileInfo(path).Length > 64_000_000) return AccountTelemetry.Empty;
            var telemetry = JsonSerializer.Deserialize<AccountTelemetry>(File.ReadAllText(path), Json);
            if (telemetry?.Samples is null || (telemetry.AccountId is { } owner && owner != accountId)) return AccountTelemetry.Empty;
            var samples = telemetry.Samples.Where(s => s is not null && s.AccountId == accountId && UsageAnalytics.IsValid(s) &&
                    s.Timestamp >= now - UsageAnalytics.Retention && s.Timestamp <= now + TimeSpan.FromMinutes(1))
                .OrderBy(s => s.Timestamp).DistinctBy(s => s.Timestamp).TakeLast(UsageAnalytics.MaximumSamplesPerAccount).ToList().AsReadOnly();
            var alerts = telemetry.Samples.All(s => s is not null && s.AccountId == accountId) ? telemetry.Alerts ?? new() : new();
            return new(samples, new(ValidateAlertState(alerts.Weekly), ValidateAlertState(alerts.Short)), accountId);
        }
        catch (JsonException) { return AccountTelemetry.Empty; }
    }

    public void SaveTelemetry(Guid accountId, AccountTelemetry telemetry) =>
        AtomicWrite(Path.Combine(Usage, $"{accountId:D}.json"), JsonSerializer.SerializeToUtf8Bytes(telemetry with { AccountId = accountId }, Json));

    public void DeleteTelemetry(Guid accountId) => File.Delete(Path.Combine(Usage, $"{accountId:D}.json"));

    private static QuotaWindowAlertState? ValidateAlertState(QuotaWindowAlertState? state) => state is not null &&
        double.IsFinite(state.Remaining) && state.Remaining is >= 0 and <= 100 ? state with { NotifiedThresholdMask = state.NotifiedThresholdMask & 7 } : null;

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
