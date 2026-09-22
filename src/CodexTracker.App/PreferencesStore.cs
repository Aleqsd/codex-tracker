using System.Text.Json;
using System.Text.Json.Serialization;
using CodexTracker.Codex;
using CodexTracker.Core;

namespace CodexTracker.App;

internal enum ThemeMode { System, Light, Dark }
internal enum SortMode { Active, Quota, Reset, Plan }
internal sealed record AccountAppearance(string? Name = null, string? AvatarFile = null);

internal sealed record TrackerPreferences
{
    public bool McpEnabled { get; init; }
    public bool DownloadUpdatesAutomatically { get; init; } = true;
    public bool InstallUpdatesAtStartup { get; init; } = true;
    public bool IncludePrereleaseUpdates { get; init; }
    public bool RecoveryPending { get; init; }
    [JsonIgnore] public bool PrivacyMode => false; // Legacy preference is ignored.
    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;
    public SortMode SortMode { get; init; } = SortMode.Active;
    public bool Alert20 { get; init; } = true;
    public bool Alert10 { get; init; } = true;
    public bool Alert5 { get; init; } = true;
    public bool ResetNotifications { get; init; } = true;
    public bool HoverPreview { get; init; } = true;
    public int RefreshMinutes { get; init; } = 2;
    public bool AdaptiveRefresh { get; init; }
    public bool ExpiryNotifications { get; init; } = true;
    public int ExpiryLeadHours { get; init; } = 24;
    public ReminderRule[]? ReminderRules { get; init; }
    public PhonePolicy PhonePolicy { get; init; } = new();
    public Dictionary<Guid, AccountAppearance> Appearances { get; init; } = new();
    public Dictionary<string, DateTimeOffset> SentExpiryReminders { get; init; } = new();
}

internal sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string? _path;
    public string DataDirectory { get; }
    public TrackerPreferences Current { get; private set; } = Normalize(new());
    public bool RecoveryRequired => Current.RecoveryPending;
    public string? RecoveryWarning => RecoveryRequired
        ? "Des préférences locales étaient illisibles. Les réglages disponibles ont été récupérés avec les rappels, alertes et assistants désactivés. Vérifiez vos réglages avant de les réactiver. Le fichier d’origine est conservé."
        : null;
    public event EventHandler? Changed;
    public string Revision { get; private set; } = Guid.NewGuid().ToString("N");
    public void Touch() { Revision = Guid.NewGuid().ToString("N"); Changed?.Invoke(this, EventArgs.Empty); }

    public PreferencesStore(bool persistent = true, string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? (persistent
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker")
            : Path.Combine(Path.GetTempPath(), "CodexTrackerDemo", Guid.NewGuid().ToString("N")));
        if (!persistent) return;
        _path = Path.Combine(DataDirectory, "preferences.json");
        var loaded = RecoverableJsonFile.Read(_path, Parse);
        Current = loaded.Value ?? Normalize(new());
        if (loaded.RecoveryRequired) Current = DisableNotifications(Current) with { RecoveryPending = true };
    }

    public void Update(Func<TrackerPreferences, TrackerPreferences> update)
    {
        var next = Normalize(update(Current)) with { RecoveryPending = Current.RecoveryPending };
        if (next == Current) return;
        Save(next);
    }

    public void AcknowledgeRecovery()
    {
        if (RecoveryRequired) Save(Current with { RecoveryPending = false });
    }

    private void Save(TrackerPreferences next)
    {
        if (_path is not null) RecoverableJsonFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(next, Json), Parse);
        Current = next;
        Touch();
    }

    private static TrackerPreferences Parse(byte[] bytes)
    {
        var value = JsonSerializer.Deserialize<TrackerPreferences>(bytes, Json) ?? throw new JsonException("Préférences absentes.");
        if (!Enum.IsDefined(value.ThemeMode)) value = value with { ThemeMode = ThemeMode.System };
        if (!Enum.IsDefined(value.SortMode)) value = value with { SortMode = SortMode.Active };
        return Normalize(value);
    }

    private static TrackerPreferences DisableNotifications(TrackerPreferences value) => value with
    {
        McpEnabled = false, Alert20 = false, Alert10 = false, Alert5 = false,
        ResetNotifications = false, ExpiryNotifications = false,
        ReminderRules = value.ReminderRules!.Select(rule => rule with { Enabled = false }).ToArray()
    };
    private static TrackerPreferences Normalize(TrackerPreferences value) => value with
    {
        RefreshMinutes = RefreshPolicy.NormalizeMinutes(value.RefreshMinutes),
        ExpiryLeadHours = ExpiryReminders.NormalizeLeadHours(value.ExpiryLeadHours),
        ReminderRules = ReminderPlanner.Normalize(value.ReminderRules ?? ReminderPlanner.Defaults(value.ExpiryNotifications, ExpiryReminders.NormalizeLeadHours(value.ExpiryLeadHours))),
        PhonePolicy = NormalizePhone(value.PhonePolicy ?? new()),
        Appearances = value.Appearances ?? new(),
        SentExpiryReminders = value.SentExpiryReminders ?? new()
    };
    private static PhonePolicy NormalizePhone(PhonePolicy policy)
    {
        try { ReminderPlanner.Zone(policy); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { policy = policy with { TimeZoneId = "Europe/Paris" }; }
        return policy with { SmsPerDay = Math.Clamp(policy.SmsPerDay, 0, 100), CallsPerDay = Math.Clamp(policy.CallsPerDay, 0, 20), QuietStart = Math.Clamp(policy.QuietStart, 0, 23), QuietEnd = Math.Clamp(policy.QuietEnd, 0, 23) };
    }
}

internal static class PrivacyText
{
    public static string Account(AccountProfile profile, TrackerState state, TrackerPreferences preferences) =>
        preferences.Appearances.GetValueOrDefault(profile.Id)?.Name is { Length: > 0 } name ? name : profile.Email;
    public static string Email(string email, bool privacy) => privacy ? "Compte masqué" : email;
    public static string Account(AccountProfile profile, TrackerState state, bool privacy)
    {
        if (!privacy) return profile.Email;
        var index = state.Accounts.Select(a => a.Profile.Id).ToList().IndexOf(profile.Id);
        return index >= 0 ? $"Compte {index + 1:00}" : "Compte masqué";
    }
}
