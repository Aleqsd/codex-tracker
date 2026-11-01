using System.IO;
using System.Text.Json;
using CodexTracker.Core;

namespace CodexTracker.App;

/// <summary>
/// English lives in C# tables; the other languages are embedded JSON files ({"French source": "text"}),
/// loaded only for the language in use. Every language falls back to English, then French.
/// </summary>
internal static class AppLocalization
{
    internal static void Apply(AppLanguage language)
    {
        Loc.Register(EnglishApp.All);
        var resolved = Loc.Resolve(language);
        if (resolved is not (AppLanguage.French or AppLanguage.English)) Loc.Register(resolved, Load(Loc.CodeOf(resolved)));
        Loc.Use(resolved);
    }

    internal static IEnumerable<(string French, string Text)> Load(string code)
    {
        using var stream = typeof(AppLocalization).Assembly.GetManifestResourceStream($"CodexTracker.Translations.{code}.json");
        if (stream is null) return [];
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        return table.Select(e => (e.Key, e.Value)).ToArray();
    }

    /// <summary>Embedded translation codes, for tests and diagnostics.</summary>
    internal static IEnumerable<string> Available => typeof(AppLocalization).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("CodexTracker.Translations.", StringComparison.Ordinal)).Select(n => n["CodexTracker.Translations.".Length..^".json".Length]);
}
