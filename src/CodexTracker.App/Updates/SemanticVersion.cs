using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace CodexTracker.App.Updates;

internal sealed record SemanticVersion(int Major, int Minor, int Patch, string? PreRelease, string Text) : IComparable<SemanticVersion>
{
    public static SemanticVersion? Parse(string? text)
    {
        if (text is null) return null;
        var match = Regex.Match(text, @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) || !int.TryParse(match.Groups[3].Value, out var patch)) return null;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(p => p.Length > 1 && p[0] == '0' && p.All(char.IsAsciiDigit)) == true) return null;
        return new(major, minor, patch, pre, $"{major}.{minor}.{patch}" + (pre is null ? "" : "-" + pre));
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;
        foreach (var difference in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) })
            if (difference != 0) return difference;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        var left = PreRelease.Split('.'); var right = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var ln = left[i].All(char.IsAsciiDigit); var rn = right[i].All(char.IsAsciiDigit);
            var result = ln && rn ? (left[i].Length != right[i].Length ? left[i].Length.CompareTo(right[i].Length) : string.CompareOrdinal(left[i], right[i]))
                : ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }
}
