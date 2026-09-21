using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexTracker.Core;

namespace CodexTracker.Codex;

public sealed record TwilioSettings(bool Enabled = false, string AccountSid = "", string KeySid = "", string Secret = "", string SmsFrom = "", string CallFrom = "", string To = "");
public sealed record SendGridSettings(bool Enabled = false, string ApiKey = "", string From = "", string To = "");
public sealed record NotificationSecrets(TwilioSettings Twilio, SendGridSettings SendGrid)
{
    public static NotificationSecrets Empty => new(new(), new());
}

public static class NotificationFiles
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class NotificationSecretStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "notification-secrets.dpapi");
    public NotificationSecrets Read()
    {
        if (!File.Exists(_path)) return NotificationSecrets.Empty;
        var bytes = Protect(File.ReadAllBytes(_path), false);
        try
        {
            var value = JsonSerializer.Deserialize<NotificationSecrets>(bytes, NotificationFiles.Json);
            if (value?.Twilio is not { AccountSid: not null, KeySid: not null, Secret: not null, SmsFrom: not null, CallFrom: not null, To: not null }
                || value.SendGrid is not { ApiKey: not null, From: not null, To: not null }) throw new InvalidDataException("Identifiants illisibles.");
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Save(NotificationSecrets secrets)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(secrets, NotificationFiles.Json);
        try { NotificationFiles.Write(_path, Protect(bytes, true)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Protect(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            var ok = encrypt ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Impossible de protéger ou lire les identifiants Windows.");
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}

public sealed class ReminderJournal
{
    private readonly string _path;
    public List<ReminderDelivery> Entries { get; private set; }
    public ReminderJournal(string directory)
    {
        _path = Path.Combine(directory, "reminder-journal.json");
        // An unreadable journal must block dispatch, never reset deduplication silently.
        Entries = File.Exists(_path) ? JsonSerializer.Deserialize<List<ReminderDelivery>>(File.ReadAllText(_path), NotificationFiles.Json) ?? throw new InvalidDataException("Journal illisible.") : [];
        if (Entries.Any(e => e?.Occurrence is not { Key: not null, AccountName: not null } || !Enum.IsDefined(e.Status) || !Enum.IsDefined(e.Occurrence.Channel) || !Enum.IsDefined(e.Occurrence.Kind)))
            throw new InvalidDataException("Journal illisible.");
        if (Entries.Any(e => e.Status == DeliveryStatus.Submitting))
        {
            Entries = Entries.Select(e => e.Status == DeliveryStatus.Submitting ? e with { Status = DeliveryStatus.Unknown, Detail = "Envoi interrompu : résultat inconnu, aucune réémission automatique." } : e).ToList();
            Save();
        }
    }
    public void Put(ReminderDelivery delivery)
    {
        var next = Entries.Where(e => e.Occurrence.Key != delivery.Occurrence.Key).Append(delivery).ToList();
        NotificationFiles.Write(_path, JsonSerializer.SerializeToUtf8Bytes(next, NotificationFiles.Json)); Entries = next;
    }
    public void Prune(DateTimeOffset now)
    {
        var next = Entries.Where(e => e.UpdatedAt > now.AddDays(-30) || e.Occurrence.At > now).ToList();
        if (next.Count == Entries.Count) return;
        NotificationFiles.Write(_path, JsonSerializer.SerializeToUtf8Bytes(next, NotificationFiles.Json)); Entries = next;
    }
    private void Save() => NotificationFiles.Write(_path, JsonSerializer.SerializeToUtf8Bytes(Entries, NotificationFiles.Json));
}
