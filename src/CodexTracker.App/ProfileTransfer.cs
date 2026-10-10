using System.Text.Json;
using System.Text.Json.Serialization;
using CodexTracker.Codex;
using CodexTracker.Core;

namespace CodexTracker.App;

/// <summary>Everything that moves to a new PC: accounts, observations, history, settings, names and avatars.</summary>
internal sealed record ProfileBundle(int Version, string AppVersion, DateTimeOffset ExportedAt, ProfileData Profiles,
    TrackerPreferences Preferences, Dictionary<string, string> Avatars);

/// <summary>
/// Password-protected profile export and import. Channel keys (DPAPI, tied to this PC), assistant access,
/// the delivery journal and Claude Code inbox stay on each PC. Imports merge and never remove anything.
/// </summary>
internal static class ProfileTransfer
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static byte[] Export(ITrackerService service, PreferencesStore preferences, string password, DateTimeOffset now)
    {
        var current = preferences.Current;
        var avatars = new Dictionary<string, string>();
        foreach (var file in current.Appearances.Values.Select(a => a.AvatarFile).OfType<string>().Distinct())
            if (AvatarStore.PathFor(preferences.DataDirectory, file) is { } path && File.Exists(path))
                avatars[file] = Convert.ToBase64String(File.ReadAllBytes(path));
        var version = typeof(ProfileTransfer).Assembly.GetName().Version?.ToString(3) ?? "";
        var bundle = new ProfileBundle(CurrentVersion, version, now, service.ExportProfiles(),
            current with { McpEnabled = false, RecoveryPending = false, ManualCodexReset = null, SentExpiryReminders = new() }, avatars);
        return ProfileArchive.Protect(JsonSerializer.SerializeToUtf8Bytes(bundle, Json), password);
    }

    public static ProfileBundle Read(byte[] archive, string password)
    {
        ProfileBundle? bundle;
        try { bundle = JsonSerializer.Deserialize<ProfileBundle>(ProfileArchive.Unprotect(archive, password), Json); }
        catch (JsonException) { throw new TrackerException(Loc.T("Ce fichier n’est pas un profil Codex Tracker.")); }
        if (bundle?.Profiles is null || bundle.Preferences is null) throw new TrackerException(Loc.T("Ce fichier n’est pas un profil Codex Tracker."));
        if (bundle.Version != CurrentVersion) throw new TrackerException(Loc.T("Ce profil vient d’une version plus récente de Codex Tracker. Mettez à jour avant de l’importer."));
        return bundle with { Preferences = PreferencesStore.Sanitize(bundle.Preferences) };
    }

    public static async Task<ProfileImport> ImportAsync(ProfileBundle bundle, ITrackerService service, ApplicationCommands commands, PreferencesStore preferences)
    {
        var result = await service.ImportProfilesAsync(bundle.Profiles);
        var avatars = new Dictionary<string, string>();
        foreach (var (file, content) in bundle.Avatars ?? [])
        {
            var temporary = Path.Combine(Path.GetTempPath(), "CodexTrackerAvatar-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(temporary, Convert.FromBase64String(content));
                // Decoded, bounded and re-encoded like an avatar chosen on this PC.
                avatars[file] = AvatarStore.Save(preferences.DataDirectory, AvatarStore.ReadImage(temporary));
            }
            catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or ArgumentException or UnauthorizedAccessException or System.IO.FileFormatException) { }
            finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
        commands.SavePreferences(current => Apply(current, bundle.Preferences, result.Accounts, avatars));
        return result;
    }

    /// <summary>The file's settings replace this PC's, except assistant access, recovery state and the manual Codex reset.</summary>
    internal static TrackerPreferences Apply(TrackerPreferences current, TrackerPreferences imported, IReadOnlyDictionary<Guid, Guid> accounts, IReadOnlyDictionary<string, string> avatars)
    {
        var appearances = new Dictionary<Guid, AccountAppearance>(current.Appearances);
        foreach (var (id, appearance) in imported.Appearances ?? [])
        {
            if (!accounts.TryGetValue(id, out var target)) continue;
            var avatar = appearance.AvatarFile is { } file && avatars.TryGetValue(file, out var copied) ? copied : appearances.GetValueOrDefault(target)?.AvatarFile;
            appearances[target] = new(appearance.Name ?? appearances.GetValueOrDefault(target)?.Name, avatar);
        }
        return imported with
        {
            McpEnabled = current.McpEnabled, RecoveryPending = current.RecoveryPending, ManualCodexReset = current.ManualCodexReset,
            SentExpiryReminders = current.SentExpiryReminders, Appearances = appearances,
            ReminderRules = imported.ReminderRules?.Select(rule => rule with
            {
                AccountIds = rule.AccountIds?.Where(accounts.ContainsKey).Select(id => accounts[id]).ToArray()
            }).Where(rule => rule.AccountIds is not { Length: 0 }).ToArray() ?? current.ReminderRules
        };
    }
}
