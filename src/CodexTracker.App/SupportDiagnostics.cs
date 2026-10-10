using System.Runtime.InteropServices;
using CodexTracker.App.Updates;
using CodexTracker.Codex;
using CodexTracker.Core;

namespace CodexTracker.App;

// Explicit projection only: never serialize preferences, tracker state, paths or exception messages.
internal static class SupportDiagnostics
{
    internal static string Create(string version, TrackerPreferences preferences, CodexCompatibilityDiagnostic? compatibility,
        bool remindersAvailable, bool assistantAvailable, WindowsNotificationState notifications, bool updateReady, bool demo)
    {
        var parsed = SemanticVersion.Parse(version);
        // Build metadata and prerelease identifiers can contain arbitrary text in developer builds.
        var safeVersion = parsed is null ? Loc.T("inconnue") : $"{parsed.Major}.{parsed.Minor}.{parsed.Patch}";
        var rows = new List<string>
        {
            $"Codex Tracker {safeVersion}" + (demo ? " · " + Loc.T("démonstration") : ""),
            Loc.F("Windows : {0} · {1}", Environment.OSVersion.Version, RuntimeInformation.OSArchitecture),
            Loc.F("Runtime .NET : {0}", Environment.Version),
            Loc.F("Mises à jour : {0}", preferences.IncludePrereleaseUpdates ? Loc.T("stables + préversions") : Loc.T("stables")),
            Loc.F("Téléchargement auto : {0} · Installation au démarrage : {1}", Yes(preferences.DownloadUpdatesAutomatically), Yes(preferences.InstallUpdatesAtStartup)),
            Loc.F("Mise à jour prête : {0}", Yes(updateReady)),
            Loc.F("Préférences à vérifier : {0}", Yes(preferences.RecoveryPending)),
            Loc.F("Moteur de rappels disponible : {0}", Yes(remindersAvailable)),
            Loc.F("État de notification Windows : {0} (ne détermine pas tous les réglages Ne pas déranger)", Known(notifications)),
            Loc.F("MCP activé : {0} · Disponible : {1}", Yes(preferences.McpEnabled), Yes(assistantAvailable))
        };
        if (compatibility is { } c)
        {
            rows.Add(Loc.F("Emplacement Codex : {0}", Known(c.HomeSource)));
            rows.Add(Loc.F("Session : {0} · App-server : {1}", Known(c.Session), Known(c.Service)));
            rows.Add(Loc.F("Dernier diagnostic : {0}", Known(c.LastFailure)));
            rows.Add(Loc.F("Vérification : {0}", Date(c.CheckedAt)));
            rows.Add(Loc.F("Dernier relevé réussi : {0}", Date(c.LastSuccessfulReadAt)));
        }
        else rows.Add(Loc.T("Compatibilité Codex : non vérifiée dans cette instance."));
        return string.Join(Environment.NewLine, rows);
    }

    private static string Yes(bool value) => value ? Loc.T("oui") : Loc.T("non");
    private static string Known<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    private static string Date(DateTimeOffset? value) => value?.UtcDateTime.ToString("dd/MM/yyyy HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? Loc.T("inconnue");
}
