using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodexTracker.Core;

public sealed record CalendarEntry(string Uid, string Title, string Description, DateTimeOffset StartsAt);

public static class CalendarExport
{
    // Google documents this link for a one-off event that the user saves in the browser.
    public static Uri GoogleEventLink(CalendarEntry entry) => new(
        "https://calendar.google.com/calendar/r/eventedit?action=TEMPLATE" +
        "&dates=" + Uri.EscapeDataString(Utc(entry.StartsAt) + "/" + Utc(entry.StartsAt.AddMinutes(5))) +
        "&stz=Etc%2FUTC&etz=Etc%2FUTC&text=" + Uri.EscapeDataString(entry.Title) +
        "&details=" + Uri.EscapeDataString(entry.Description));

    public static string Identity(Guid accountId, string kind, string id, DateTimeOffset at) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{accountId:N}|{kind}|{id}|{at.ToUnixTimeSeconds()}"))).ToLowerInvariant();

    public static IReadOnlyList<CalendarEntry> Entries(TrackerState state, Func<AccountProfile, string> name, DateTimeOffset now)
    {
        var result = new List<CalendarEntry>();
        foreach (var account in state.Accounts)
        {
            if (account.Snapshot is not { } snapshot) continue;
            var description = $"Date prévue selon le relevé du {snapshot.FetchedAt:dd/MM/yyyy HH:mm:ss zzz}. " +
                "Export ponctuel : les modifications ultérieures ne sont pas synchronisées. Vérifiez dans Codex.";
            void Add(string kind, string id, string title, DateTimeOffset? date)
            {
                if (date is not { } at || at <= now) return;
                result.Add(new(Identity(account.Profile.Id, kind, id, at) + "@codex-tracker.local", $"{name(account.Profile)} · {title}", description, at));
            }
            Add("quota", "weekly", "Reset Codex · semaine", snapshot.Weekly?.ResetsAt);
            Add("quota", "short", "Reset Codex · 5 heures", snapshot.Short?.ResetsAt);
            if (snapshot.AvailableResetCredits > 0)
                foreach (var credit in snapshot.ResetCredits ?? []) Add("credit", credit.Id, "Expiration d’un reset en réserve", credit.ExpiresAt);
        }
        return result.DistinctBy(e => e.Uid).OrderBy(e => e.StartsAt).ToArray();
    }

    public static string Serialize(IEnumerable<CalendarEntry> entries, DateTimeOffset now)
    {
        var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Codex Tracker//Calendrier//FR", "CALSCALE:GREGORIAN" };
        foreach (var entry in entries)
        {
            lines.AddRange(["BEGIN:VEVENT", "UID:" + Escape(entry.Uid), "DTSTAMP:" + Utc(now),
                "DTSTART:" + Utc(entry.StartsAt), "DTEND:" + Utc(entry.StartsAt.AddMinutes(5)),
                "SUMMARY:" + Escape(entry.Title), "DESCRIPTION:" + Escape(entry.Description), "TRANSP:TRANSPARENT", "END:VEVENT"]);
        }
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }
    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string Fold(string line)
    {
        var output = new StringBuilder(); var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 75) { output.Append("\r\n "); bytes = 1; }
            output.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return output.ToString();
    }
}
