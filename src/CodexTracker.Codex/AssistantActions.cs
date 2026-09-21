using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexTracker.Codex;

public sealed record AssistantAction(string Id, string Fingerprint, string Status, DateTimeOffset ExpiresAt, string Detail);

// Durable idempotency receipts contain no arguments or credentials. Pending payloads live only in memory.
// A process interruption never automatically repeats a possibly accepted external test.
public sealed class AssistantActions
{
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private List<AssistantAction> _items;
    public AssistantActions(string? directory, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _path = directory is null ? null : Path.Combine(directory, "assistant-actions.json");
        _items = _path is not null && File.Exists(_path) ? JsonSerializer.Deserialize<List<AssistantAction>>(File.ReadAllText(_path), NotificationFiles.Json)
            ?? throw new InvalidDataException("Journal assistant illisible.") : [];
        if (_items.Any(a => a is null || !Guid.TryParse(a.Id, out _) || a.Fingerprint is null || a.Status is null)) throw new InvalidDataException("Journal assistant illisible.");
        _items = _items.Select(a => a.Status == "pending" ? a with { Status = "cancelled", Detail = "Application redémarrée." }
            : a.Status == "executing" ? a with { Status = "unknown", Detail = "Action interrompue ; aucune répétition automatique." } : a).ToList();
        Save();
    }
    public AssistantAction? Find(string id)
    {
        if (Guid.TryParse(id, out var parsed)) id = parsed.ToString("D");
        var item = _items.FirstOrDefault(a => a.Id == id);
        if (item is { Status: "pending" } && item.ExpiresAt <= _clock.GetUtcNow()) return Set(id, "expired", "Confirmation expirée.");
        return item;
    }
    public static string Fingerprint(string method, string arguments) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method + "\n" + arguments)));
    public AssistantAction Begin(string id, string fingerprint, bool confirm, string detail)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new ArgumentException("requestId doit être un UUID.");
        if (Find(id) is { } prior)
        {
            if (prior.Fingerprint != fingerprint) throw new InvalidOperationException("request_id_conflict");
            return prior;
        }
        if (_items.Count > 10000) throw new InvalidOperationException("Journal assistant plein.");
        var item = new AssistantAction(id, fingerprint, confirm ? "pending" : "executing", _clock.GetUtcNow().AddMinutes(5), detail);
        _items.Add(item); Save(); return item;
    }
    public AssistantAction Set(string id, string status, string detail)
    {
        var index = _items.FindIndex(a => a.Id == id);
        if (index < 0) throw new ArgumentException("Action inconnue.");
        var next = _items[index] with { Status = status, Detail = detail };
        var previous = _items[index]; _items[index] = next;
        try { Save(); } catch { _items[index] = previous; throw; }
        return next;
    }
    public void CancelPending(string detail)
    {
        foreach (var a in _items.Where(a => a.Status == "pending").ToArray()) Set(a.Id, "cancelled", detail);
    }
    private void Save()
    {
        // Retain receipts: removing them would make old request IDs replayable.
        if (_path is not null) NotificationFiles.Write(_path, JsonSerializer.SerializeToUtf8Bytes(_items, NotificationFiles.Json));
    }
}
