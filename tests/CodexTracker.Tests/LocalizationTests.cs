using System.Net;
using System.Text.RegularExpressions;
using CodexTracker.App;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class LocalizationTests
{
    private static readonly Regex CodeText = new(@"Loc\.[TF]\(\s*""((?:[^""\\\r\n]|\\.)*)""", RegexOptions.Compiled);
    private static readonly Regex XamlText = new(@"\{local:T '([^']*)'\}", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    static LocalizationTests() => Loc.Register(EnglishApp.All);

    [Fact]
    public void EveryMarkedFrenchTextHasAnEnglishEntry()
    {
        var missing = SourceTexts().Where(text => !Loc.Has(text.French)).Select(text => $"{text.File}: {text.French}").Distinct().ToArray();
        Assert.True(missing.Length == 0, "Textes sans traduction anglaise :\n" + string.Join("\n", missing));
    }

    [Fact]
    public void EnglishTemplatesKeepTheFrenchPlaceholders()
    {
        var mismatched = SourceTexts().Select(text => text.French).Distinct()
            .Where(french => Loc.EnglishFor(french) is { } english && !Indexes(french).SetEquals(Indexes(english)))
            .ToArray();
        Assert.True(mismatched.Length == 0, "Paramètres différents :\n" + string.Join("\n", mismatched));
    }

    [Fact]
    public void FrenchRemainsTheSourceAndTheFallback()
    {
        Assert.False(Loc.IsEnglish);
        Assert.Equal("Texte sans traduction", Loc.T("Texte sans traduction"));
        Assert.Equal(AppLanguage.French, Loc.Resolve(AppLanguage.French));
        Assert.Equal(AppLanguage.English, Loc.Resolve(AppLanguage.English));
        Assert.True(SourceTexts().Count() > 100, "Les textes de l’interface doivent passer par Loc.");
    }

    [Fact]
    public void EveryEnglishEntryIsStillUsedAndRegisteredOnce()
    {
        var entries = EnglishEntries().ToArray();
        var duplicates = entries.GroupBy(e => e.French).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        var source = Path.Combine(Root(), "src");
        // Any C# literal counts, including keys kept in a variable before Loc.T or inside interpolated strings.
        var code = string.Join("\n", Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => Path.GetFileName(f) is not "Localization.English.cs" && !Path.GetFileName(f).StartsWith("EnglishApp", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var markup = SourceTexts().Select(text => text.French).ToHashSet();
        var unused = entries.Select(e => e.French).Distinct().Where(french => !markup.Contains(french) && !code.Contains('"' + Literal(french) + '"')).ToArray();
        Assert.True(duplicates.Length == 0, "Textes enregistrés plusieurs fois :\n" + string.Join("\n", duplicates));
        Assert.True(unused.Length == 0, "Traductions inutilisées :\n" + string.Join("\n", unused));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("pt")]
    [InlineData("ja")]
    public void EveryLanguageTranslatesEachEnglishEntryWithTheSamePlaceholders(string code)
    {
        Assert.Contains(code, AppLocalization.Available);
        var language = Loc.FromCode(code)!.Value;
        var english = EnglishEntries().Select(e => e.French).ToHashSet();
        var table = AppLocalization.Load(code).ToArray();
        var keys = table.Select(e => e.French).ToHashSet();
        var missing = english.Where(f => !keys.Contains(f)).ToArray();
        var orphans = keys.Where(f => !english.Contains(f)).ToArray();
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language switch { AppLanguage.Spanish => "es-ES", AppLanguage.German => "de-DE", AppLanguage.Portuguese => "pt-BR", _ => "ja-JP" });
        var broken = table.Where(e => !Indexes(e.French).SetEquals(Indexes(e.Text)) || !Formats(e.French, e.Text, culture) || e.Text.Trim().Length == 0 && e.French.Trim().Length > 0)
            .Select(e => $"{e.French} → {e.Text}").ToArray();
        Assert.True(missing.Length == 0, $"Textes sans traduction ({code}) :\n" + string.Join("\n", missing));
        Assert.True(orphans.Length == 0, $"Traductions orphelines ({code}) :\n" + string.Join("\n", orphans));
        Assert.True(broken.Length == 0, $"Traductions invalides ({code}) :\n" + string.Join("\n", broken));
    }

    [Fact]
    public void LanguagesResolveFromTheirCodeAndFallBackToEnglish()
    {
        foreach (var (language, code, _) in Loc.Languages) Assert.Equal(language, Loc.FromCode(code));
        Assert.Null(Loc.FromCode("xx"));
        Loc.Register(AppLanguage.German, [("Texte de test traduit", "Übersetzter Testtext")]);
        Loc.Register([("Texte de test anglais", "English test text")]);
        Assert.Equal("Übersetzter Testtext", Loc.For(AppLanguage.German, "Texte de test traduit"));
        Assert.Equal("English test text", Loc.EnglishFor("Texte de test anglais"));
    }

    private static IEnumerable<(string French, string English)> EnglishEntries()
    {
        var core = (ValueTuple<string, string>[])typeof(Loc).Assembly.GetType("CodexTracker.Core.EnglishCore")!
            .GetField("Entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        return EnglishApp.All.Concat(core.Select(e => (French: e.Item1, English: e.Item2)));
    }

    /// <summary>The translation formats with arguments that suit the French template, so a stray brace fails here, not on screen.</summary>
    private static bool Formats(string french, string text, IFormatProvider culture)
    {
        var specs = Placeholder.Matches(french).GroupBy(m => int.Parse(m.Groups[1].Value)).ToDictionary(g => g.Key, g => g.First().Value);
        if (specs.Count == 0) return true;
        var samples = new object[] { DateTimeOffset.Parse("2026-09-21T12:00:00Z"), 42, TimeSpan.FromHours(2), "texte" };
        var args = new object?[specs.Keys.Max() + 1];
        foreach (var (index, spec) in specs)
            args[index] = samples.FirstOrDefault(sample => { try { string.Format(culture, "{0" + spec[(1 + index.ToString().Length)..], sample); return true; } catch (FormatException) { return false; } }) ?? "texte";
        try { string.Format(culture, text, args); return true; } catch (FormatException) { return false; }
    }

    /// <summary>The text as written between the quotes of a regular C# literal.</summary>
    private static string Literal(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static HashSet<string> Indexes(string template) => Placeholder.Matches(template).Select(m => m.Groups[1].Value).ToHashSet();

    private static IEnumerable<(string File, string French)> SourceTexts()
    {
        var source = Path.Combine(Root(), "src");
        foreach (var file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (Match match in CodeText.Matches(File.ReadAllText(file)))
                yield return (Path.GetRelativePath(source, file), Regex.Unescape(match.Groups[1].Value));
        foreach (var file in Directory.EnumerateFiles(source, "*.xaml", SearchOption.AllDirectories))
            foreach (Match match in XamlText.Matches(File.ReadAllText(file)))
                yield return (Path.GetRelativePath(source, file), WebUtility.HtmlDecode(match.Groups[1].Value));
    }

    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CodexTracker.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
