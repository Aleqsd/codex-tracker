namespace CodexTracker.Core;

public sealed record ResetCalendarDay(DateOnly Date, IReadOnlyList<ResetScheduleEntry> Entries);

/// <summary>Calendar dates are local dates, while ordering and expiration use actual instants.</summary>
public static class ResetCalendar
{
    public static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static IReadOnlyList<ResetCalendarDay> Week(IEnumerable<ResetScheduleEntry> entries, DateOnly date, TimeZoneInfo zone)
    {
        var monday = Monday(date);
        var dated = entries.Where(e => e.At is not null)
            .GroupBy(e => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.At!.Value, zone).DateTime))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ResetScheduleEntry>)g.OrderBy(e => e.At).ToArray());
        return Enumerable.Range(0, 7).Select(offset =>
        {
            var day = monday.AddDays(offset);
            return new ResetCalendarDay(day, dated.GetValueOrDefault(day) ?? []);
        }).ToArray();
    }

    // A detailed credit does not establish availability: the server count must be known and positive.
    // The returned account retains its observation date/error; this is a priority at the last reading.
    public static ResetScheduleEntry? PriorityReserve(TrackerState state, DateTimeOffset now, Guid? accountId = null) =>
        ResetSchedule.Entries(state, accountId)
            .Where(e => e.Kind == ResetKind.Reserve && !e.IsUndetailedReserve && e.At > now
                && e.Account.Snapshot?.AvailableResetCredits > 0)
            .OrderBy(e => e.At).ThenBy(e => e.Account.Profile.Id).ThenBy(e => e.CreditId, StringComparer.Ordinal)
            .FirstOrDefault();
}
