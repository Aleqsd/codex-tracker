using System.Globalization;

namespace CodexTracker.Core;

/// <summary>New languages are appended: preferences store the name, older values keep their meaning.</summary>
public enum AppLanguage { System, French, English, Spanish, German, Portuguese, Japanese }

/// <summary>
/// French is the source language and the lookup key, so the French interface never depends on a table.
/// Other languages are resolved at display time: a missing entry falls back to English, then to French.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<AppLanguage, Dictionary<string, string>> Tables = new();

    static Loc() => Register(EnglishCore.Entries);

    /// <summary>Languages offered in the interface, with their own name and two-letter code.</summary>
    public static IReadOnlyList<(AppLanguage Language, string Code, string Name)> Languages { get; } =
    [
        (AppLanguage.French, "fr", "Français"), (AppLanguage.English, "en", "English"), (AppLanguage.Spanish, "es", "Español"),
        (AppLanguage.German, "de", "Deutsch"), (AppLanguage.Portuguese, "pt", "Português (Brasil)"), (AppLanguage.Japanese, "ja", "日本語"),
    ];

    public static AppLanguage Current { get; private set; } = AppLanguage.French;
    public static bool IsEnglish => Current == AppLanguage.English;
    public static string Code => CodeOf(Current);
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Current switch
    {
        AppLanguage.English => "en-GB", AppLanguage.Spanish => "es-ES", AppLanguage.German => "de-DE",
        AppLanguage.Portuguese => "pt-BR", AppLanguage.Japanese => "ja-JP", _ => "fr-FR",
    });

    /// <summary>Applied once at startup; the interface is not rebuilt live.</summary>
    public static void Use(AppLanguage language) => Current = Resolve(language);
    public static AppLanguage Resolve(AppLanguage language) => language != AppLanguage.System ? language
        : FromCode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? AppLanguage.English;

    public static string CodeOf(AppLanguage language) => Languages.FirstOrDefault(l => l.Language == Resolve(language)).Code ?? "fr";
    public static AppLanguage? FromCode(string? code) => Languages.Where(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)).Select(l => (AppLanguage?)l.Language).FirstOrDefault();

    public static void Register(IEnumerable<(string French, string English)> entries) => Register(AppLanguage.English, entries);
    public static void Register(AppLanguage language, IEnumerable<(string French, string Text)> entries)
    {
        lock (Tables)
        {
            if (!Tables.TryGetValue(language, out var table)) Tables[language] = table = new(StringComparer.Ordinal);
            foreach (var (french, text) in entries) table[french] = text;
        }
    }
    public static bool Has(string french) => For(AppLanguage.English, french) is not null;
    public static string? EnglishFor(string french) => For(AppLanguage.English, french);
    public static string? For(AppLanguage language, string french) { lock (Tables) return Tables.TryGetValue(language, out var table) ? table.GetValueOrDefault(french) : null; }

    public static string T(string french) => Current == AppLanguage.French ? french : For(Current, french) ?? EnglishFor(french) ?? french;
    /// <summary>Composite format: the French template is the key, placeholders keep their indexes in every language.</summary>
    public static string F(string french, params object?[] args) => string.Format(Culture, T(french), args);
}
