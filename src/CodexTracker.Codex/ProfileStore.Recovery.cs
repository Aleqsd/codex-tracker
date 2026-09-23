using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed partial class ProfileStore
{
    // One migration per source, not an automatic resurrection at every start.
    // The source is retained untouched; intentional removals after migration stay removed.
    internal void RecoverRedirectedStores(IReadOnlyList<string> sources)
    {
        if (sources.Count == 0) return;
        var ledgerPath = Path.Combine(_root, "storage-migrations.json");
        var ledger = RecoverableJsonFile.Read(ledgerPath, ParseLedger);
        if (ledger.RecoveryRequired)
            throw new TrackerException("Le journal de récupération est illisible. Vos comptes sont conservés ; aucune nouvelle fusion automatique n’a été tentée.");
        var completed = ledger.Value ?? [];
        foreach (var source in sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(source, _root, StringComparison.OrdinalIgnoreCase) || completed.Contains(source, StringComparer.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(source, "settings.json")) && !File.Exists(Path.Combine(source, "settings.json.bak"))) continue;
            // An older tracker must not write the source while we take the recovery snapshot.
            using var lease = new FileStream(Path.Combine(source, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var old = new ProfileStore(source);
            var incoming = old.LoadSettings();
            var oldSnapshots = old.LoadSnapshots();
            var current = LoadSettings();
            var snapshots = LoadSnapshots();
            var accounts = current.Accounts.ToList();
            var detected = new HashSet<Guid>(current.DetectedAccountIds ?? []);
            var backup = Path.Combine(_root, "recovery", "storage-" + Guid.NewGuid().ToString("N"));
            SecureDirectory(backup);
            BackupDisplayStore(_root, Path.Combine(backup, "before"));
            BackupDisplayStore(source, Path.Combine(backup, "source"));
            foreach (var profile in incoming.Accounts)
            {
                var target = accounts.FirstOrDefault(p => string.Equals(p.Email.Trim(), profile.Email.Trim(), StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    target = profile with { Id = accounts.Any(p => p.Id == profile.Id) ? Guid.NewGuid() : profile.Id };
                    accounts.Add(target);
                }
                if (incoming.DetectedAccountIds?.Contains(profile.Id) == true) detected.Add(target.Id);
                if (oldSnapshots.TryGetValue(profile.Id, out var snapshot) &&
                    string.Equals(snapshot.Email.Trim(), profile.Email.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    (!snapshots.TryGetValue(target.Id, out var existing) || snapshot.FetchedAt > existing.FetchedAt))
                    snapshots[target.Id] = snapshot;
                var now = DateTimeOffset.UtcNow;
                var previous = old.LoadTelemetry(profile.Id, now);
                var latest = LoadTelemetry(target.Id, now);
                if (previous.Samples.Count > 0)
                {
                    var merged = latest.Samples.Concat(previous.Samples.Select(s => s with { AccountId = target.Id }))
                        .DistinctBy(s => s.Timestamp).OrderBy(s => s.Timestamp).TakeLast(UsageAnalytics.MaximumSamplesPerAccount).ToArray();
                    SaveTelemetry(target.Id, new(merged, latest.Samples.Count > 0 ? latest.Alerts : previous.Alerts, target.Id));
                }
            }
            // Snapshots first: an interrupted migration can be retried without losing
            // the original sources or publishing accounts whose snapshots were not saved.
            SaveSnapshots(snapshots);
            SaveSettings(current with { Accounts = accounts, DetectedAccountIds = detected.ToList(),
                OnboardingComplete = current.OnboardingComplete || incoming.OnboardingComplete });
            completed.Add(source);
            RecoverableJsonFile.Write(ledgerPath, JsonSerializer.SerializeToUtf8Bytes(completed, Json), ParseLedger);
            Warn("Les comptes et relevés d’un ancien stockage Windows ont été réunis. Une copie des données d’origine est conservée localement.");
        }
    }

    private static List<string> ParseLedger(byte[] bytes)
    {
        var paths = JsonSerializer.Deserialize<List<string>>(bytes, Json) ?? throw new JsonException();
        if (paths.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p))) throw new JsonException();
        return paths;
    }

    private static void BackupDisplayStore(string source, string destination)
    {
        SecureDirectory(destination);
        foreach (var name in new[] { "settings.json", "settings.json.bak", "snapshots.json", "snapshots.json.bak", "preferences.json", "preferences.json.bak" })
            if (File.Exists(Path.Combine(source, name))) File.Copy(Path.Combine(source, name), Path.Combine(destination, name));
        var usage = Path.Combine(source, "usage");
        if (!Directory.Exists(usage)) return;
        SecureDirectory(Path.Combine(destination, "usage"));
        foreach (var file in Directory.EnumerateFiles(usage, "*.json"))
            File.Copy(file, Path.Combine(destination, "usage", Path.GetFileName(file)));
    }
}
