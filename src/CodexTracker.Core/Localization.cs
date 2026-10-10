using System.Globalization;

namespace CodexTracker.Core;

public enum AppLanguage { System, French, English }

/// <summary>
/// French is the source language and the lookup key, so the French interface never depends on the table.
/// English text is resolved at display time; a missing entry falls back to French.
/// </summary>
public static class Loc
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal);

    static Loc() => Register(EnglishCore.Entries);

    public static bool IsEnglish { get; private set; }
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-GB" : "fr-FR");

    /// <summary>Applied once at startup; the interface is not rebuilt live.</summary>
    public static void Use(AppLanguage language) => IsEnglish = Resolve(language) == AppLanguage.English;
    public static AppLanguage Resolve(AppLanguage language) => language != AppLanguage.System ? language
        : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fr" ? AppLanguage.French : AppLanguage.English;

    public static void Register(IEnumerable<(string French, string English)> entries)
    {
        lock (Table) foreach (var (french, english) in entries) Table[french] = english;
    }
    public static bool Has(string french) { lock (Table) return Table.ContainsKey(french); }
    public static string? EnglishFor(string french) { lock (Table) return Table.GetValueOrDefault(french); }

    public static string T(string french) => IsEnglish && EnglishFor(french) is { } english ? english : french;
    /// <summary>Composite format: the French template is the key, placeholders keep their indexes in English.</summary>
    public static string F(string french, params object?[] args) => string.Format(Culture, T(french), args);
}
