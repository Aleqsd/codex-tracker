using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ResetCalendarTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
    private static AccountState Account(int? available = 2, params ResetCredit[] credits) => new(
        new(Guid.NewGuid(), "fiction@example.test"), new("fiction@example.test", "plus", [], available, credits, Now.AddDays(-2)));
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    [Theory]
    [InlineData("2026-09-21", "2026-09-21")]
    [InlineData("2026-09-27", "2026-09-21")]
    [InlineData("2027-01-01", "2026-12-28")]
    public void WeekStartsMondayAndIncludesEmptyDays(string anchor, string start)
    {
        var days = ResetCalendar.Week([], DateOnly.Parse(anchor), Paris);
        Assert.Equal(7, days.Count);
        Assert.Equal(DateOnly.Parse(start), days[0].Date);
        Assert.Equal(days[0].Date.AddDays(6), days[6].Date);
        Assert.All(days, d => Assert.Empty(d.Entries));
    }

    [Fact]
    public void RepeatedAutumnHourRetainsBothActualInstantsInOrder()
    {
        var account = Account();
        var early = new ResetScheduleEntry(account, ResetKind.Short, DateTimeOffset.Parse("2026-10-25T00:30:00Z"));
        var late = early with { At = DateTimeOffset.Parse("2026-10-25T01:30:00Z") };
        var days = ResetCalendar.Week([late, early, early with { At = null }], new(2026, 10, 25), Paris);
        Assert.Equal(new[] { early, late }, days[6].Entries);
        Assert.Equal(2, days.Sum(d => d.Entries.Count));
    }

    [Theory]
    [InlineData("2026-03-28T23:30:00Z", "2026-03-29")]
    [InlineData("2026-03-29T01:30:00Z", "2026-03-29")]
    [InlineData("2026-03-29T22:00:00Z", "2026-03-30")]
    public void LocalDayBoundariesRespectSpringOffset(string instant, string date)
    {
        var entry = new ResetScheduleEntry(Account(), ResetKind.Weekly, DateTimeOffset.Parse(instant));
        var localDate = DateOnly.Parse(date);
        var day = Assert.Single(ResetCalendar.Week([entry], localDate, Paris), d => d.Entries.Count > 0);
        Assert.Equal(localDate, day.Date);
        Assert.Single(day.Entries);
    }

    [Fact]
    public void WeekExcludesOutsideDatesAndNeverRecursAnObservedQuota()
    {
        var account = Account();
        var entries = new[] { new ResetScheduleEntry(account, ResetKind.Weekly, Now), new(account, ResetKind.Reserve, Now.AddDays(8)), new(account, ResetKind.Short, null) };
        var week = ResetCalendar.Week(entries, new(2026, 10, 24), Paris);
        Assert.Equal(1, week.Sum(d => d.Entries.Count));
        Assert.DoesNotContain(ResetCalendar.Week(entries, new(2026, 10, 31), Paris).SelectMany(d => d.Entries), e => e.Kind == ResetKind.Weekly);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void PriorityRequiresPositiveKnownServerCount(int? count)
    {
        var account = Account(count, new ResetCredit("credit", "Reserve", null, Now.AddHours(1)));
        Assert.Null(ResetCalendar.PriorityReserve(new([account], null), Now));
    }

    [Fact]
    public void PrioritySkipsExpiredAndUnknownCreditsAndPreservesStaleObservation()
    {
        var account = Account(3, new("old", null, null, Now), new("unknown", null, null, null), new("next", "Priority", null, Now.AddHours(1))) with { Error = "Offline" };
        var state = new TrackerState([account, Account(1, new ResetCredit("later", null, null, Now.AddDays(2)))], null);
        var priority = ResetCalendar.PriorityReserve(state, Now);
        Assert.NotNull(priority);
        Assert.Equal("next", priority.CreditId);
        Assert.Equal(account, priority.Account);
        Assert.Null(ResetCalendar.PriorityReserve(state, Now.AddHours(1), account.Profile.Id));
        Assert.Null(ResetCalendar.PriorityReserve(state, Now, Guid.NewGuid()));
        Assert.Equal("later", ResetCalendar.PriorityReserve(state, Now, state.Accounts[1].Profile.Id)!.CreditId);
    }
}
