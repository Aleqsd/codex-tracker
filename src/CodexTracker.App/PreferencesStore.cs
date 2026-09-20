using System.Text.Json;
using System.Text.Json.Serialization;
using CodexTracker.Core;

namespace CodexTracker.App;

internal enum ThemeMode { System, Light, Dark }
internal enum SortMode { Active, Quota, Reset, Plan }

internal sealed record TrackerPreferences
{
    public bool PrivacyMode { get; init; }
    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;
    public SortMode SortMode { get; init; } = SortMode.Active;
    public bool Alert20 { get; init; } = true;
    public bool Alert10 { get; init; } = true;
    public bool Alert5 { get; init; } = true;
    public bool ResetNotifications { get; init; } = true;
    public bool HoverPreview { get; init; } = true;
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
    public TrackerPreferences Current { get; private set; } = new();
    public event EventHandler? Changed;

    public PreferencesStore(bool persistent = true, string? dataDirectory = null)
    {
        if (!persistent) return;
        _path = Path.Combine(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexTracker"), "preferences.json");
        try
        {
            if (File.Exists(_path)) Current = JsonSerializer.Deserialize<TrackerPreferences>(File.ReadAllText(_path), Json) ?? throw new JsonException("Préférences absentes.");
            if (!Enum.IsDefined(Current.ThemeMode)) Current = Current with { ThemeMode = ThemeMode.System };
            if (!Enum.IsDefined(Current.SortMode)) Current = Current with { SortMode = SortMode.Active };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable preference file must not expose account names on screen.
            Current = new() { PrivacyMode = true };
        }
    }

    public void Update(Func<TrackerPreferences, TrackerPreferences> update)
    {
        var next = update(Current);
        if (next == Current) return;
        if (_path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, next, Json);
                    stream.Flush(true);
                }
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal static class PrivacyText
{
    public static string Email(string email, bool privacy) => privacy ? "Compte masqué" : email;
    public static string Account(AccountProfile profile, TrackerState state, bool privacy)
    {
        if (!privacy) return profile.Email;
        var index = state.Accounts.Select(a => a.Profile.Id).ToList().IndexOf(profile.Id);
        return index >= 0 ? $"Compte {index + 1:00}" : "Compte masqué";
    }
}
