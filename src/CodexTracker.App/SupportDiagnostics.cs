using System.Runtime.InteropServices;
using CodexTracker.App.Updates;
using CodexTracker.Codex;

namespace CodexTracker.App;

// Explicit projection only: never serialize preferences, tracker state, paths or exception messages.
internal static class SupportDiagnostics
{
    internal static string Create(string version, TrackerPreferences preferences, CodexCompatibilityDiagnostic? compatibility,
        bool remindersAvailable, bool assistantAvailable, WindowsNotificationState notifications, bool updateReady, bool demo)
    {
        var parsed = SemanticVersion.Parse(version);
        // Build metadata and prerelease identifiers can contain arbitrary text in developer builds.
        var safeVersion = parsed is null ? "inconnue" : $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}";
        var rows = new List<string>
        {
            $"Codex Tracker {safeVersion}" + (demo ? " · démonstration" : ""),
            $"Windows : {Environment.OSVersion.Version} · {RuntimeInformation.OSArchitecture}",
            $"Runtime .NET : {Environment.Version}",
            $"Mises à jour : {(preferences.IncludePrereleaseUpdates ? "stables + préversions" : "stables")}",
            $"Téléchargement auto : {Yes(preferences.DownloadUpdatesAutomatically)} · Installation au démarrage : {Yes(preferences.InstallUpdatesAtStartup)}",
            $"Mise à jour prête : {Yes(updateReady)}",
            $"Préférences à vérifier : {Yes(preferences.RecoveryPending)}",
            $"Moteur de rappels disponible : {Yes(remindersAvailable)}",
            $"État de notification Windows : {Known(notifications)} (ne détermine pas tous les réglages Ne pas déranger)",
            $"MCP activé : {Yes(preferences.McpEnabled)} · Disponible : {Yes(assistantAvailable)}"
        };
        if (compatibility is { } c)
        {
            rows.Add($"Emplacement Codex : {Known(c.HomeSource)}");
            rows.Add($"Session : {Known(c.Session)} · App-server : {Known(c.Service)}");
            rows.Add($"Dernier diagnostic : {Known(c.LastFailure)}");
            rows.Add($"Vérification : {Date(c.CheckedAt)}");
            rows.Add($"Dernier relevé réussi : {Date(c.LastSuccessfulReadAt)}");
        }
        else rows.Add("Compatibilité Codex : non vérifiée dans cette instance.");
        return string.Join(Environment.NewLine, rows);
    }

    private static string Yes(bool value) => value ? "oui" : "non";
    private static string Known<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    private static string Date(DateTimeOffset? value) => value?.UtcDateTime.ToString("dd/MM/yyyy HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "inconnue";
}
