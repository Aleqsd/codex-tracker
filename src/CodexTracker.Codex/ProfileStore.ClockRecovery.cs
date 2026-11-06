using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.Codex;

internal sealed partial class ProfileStore
{
    private readonly HashSet<Guid> _clockRecoveryPreserved = [];

    private void DeleteClockRecovery(Guid accountId)
    {
        var directory = Path.Combine(_root, "clock-recovery");
        if (Directory.Exists(directory))
        {
            File.Delete(Path.Combine(directory, $"{accountId:D}.snapshot.json"));
            File.Delete(Path.Combine(directory, $"{accountId:D}.usage.json"));
        }
        _clockRecoveryPreserved.Remove(accountId);
    }

    // Preserve the first affected originals in a fixed pair per account. Neither retries nor restarts
    // overwrite that evidence or accumulate files. Working telemetry can then resume at its real date.
    internal void PreserveClockRecovery(Guid accountId, AccountSnapshot? snapshot)
    {
        if (_clockRecoveryPreserved.Contains(accountId)) return;
        var directory = Path.Combine(_root, "clock-recovery");
        SecureDirectory(directory);
        var archivedSnapshot = Path.Combine(directory, $"{accountId:D}.snapshot.json");
        if (snapshot is not null && !File.Exists(archivedSnapshot))
            AtomicWrite(archivedSnapshot, JsonSerializer.SerializeToUtf8Bytes(snapshot, Json));
        var usage = Path.Combine(Usage, $"{accountId:D}.json");
        var archivedUsage = Path.Combine(directory, $"{accountId:D}.usage.json");
        if (File.Exists(usage) && !File.Exists(archivedUsage))
        {
            if (new FileInfo(usage).Length > 64_000_000) throw new IOException("Local telemetry is too large to preserve.");
            AtomicWrite(archivedUsage, File.ReadAllBytes(usage));
        }
        _clockRecoveryPreserved.Add(accountId);
    }
}
