using System.Text.RegularExpressions;

namespace CodexTracker.App;

internal static class AccountAvatar
{
    private static readonly Brush[] Colors = [Display.Brush("#119F8A"), Display.Brush("#5279B7"), Display.Brush("#8B66AD"), Display.Brush("#AD6850"), Display.Brush("#61814B"), Display.Brush("#B15B7C")];

    public static Brush Background(Guid id)
    {
        // Stable across app launches, aliases and theme changes.
        uint hash = 2166136261;
        foreach (var value in id.ToByteArray()) hash = unchecked((hash ^ value) * 16777619);
        return Colors[hash % Colors.Length];
    }

    public static string Initials(string? name)
    {
        var words = Regex.Matches((name ?? "").Split('@')[0], @"\p{L}+").Select(m => m.Value).ToArray();
        if (words.Length == 0) return "?";
        return (words.Length > 1 ? $"{words[0][0]}{words[1][0]}" : words[0][..Math.Min(2, words[0].Length)]).ToUpperInvariant();
    }
}
