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
        var core = (ValueTuple<string, string>[])typeof(Loc).Assembly.GetType("CodexTracker.Core.EnglishCore")!
            .GetField("Entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var entries = EnglishApp.All.Concat(core.Select(e => (French: e.Item1, English: e.Item2))).ToArray();
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
