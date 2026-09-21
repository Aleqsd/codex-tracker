using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ResetScheduleTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T00:30:00Z");
    private static readonly AccountProfile Profile = new(Guid.NewGuid(), "studio@example.test");
    private static AccountSnapshot Snapshot => new(Profile.Email, "plus",
        [new("codex", null, [new(20, 300, Now.AddHours(1)), new(30, 10080, Now.AddDays(2))])], 2,
        [new("old", "Ancien", Now.AddDays(-30), Now.AddDays(-1)), new("next", "Prochain", Now.AddDays(-3), Now.AddDays(1))], Now);
    private static TrackerState State(AccountSnapshot? snapshot) => new([new(Profile, snapshot)], Profile.Id);

    [Fact]
    public void ScheduleOrdersRealInstantsAcrossKindsAndPreservesGrantDates()
    {
        var rows = ResetSchedule.Entries(State(Snapshot));
        Assert.Equal(new[] { ResetKind.Reserve, ResetKind.Short, ResetKind.Reserve, ResetKind.Weekly }, rows.Select(r => r.Kind));
        Assert.Equal(Now.AddDays(-30), rows[0].GrantedAt);
        Assert.Equal("Ancien", rows[0].CreditTitle);
        Assert.Equal(Now.AddDays(-1), rows[0].At);
        Assert.Equal(Now.AddDays(2), rows[3].At);
        Assert.All(rows, row => Assert.Equal(Snapshot.FetchedAt, row.Account.Snapshot!.FetchedAt));
    }

    [Fact]
    public void MissingSnapshotRemainsUnknownForAllThreeKinds()
    {
        var rows = ResetSchedule.Entries(State(null));
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Null(row.At));
        Assert.Null(rows[0].Account.Snapshot);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    [InlineData(null, 1)]
    public void UndetailedReservesNeverInventIndividualExpirationDates(int? count, int placeholders)
    {
        var rows = ResetSchedule.Entries(State(Snapshot with { AvailableResetCredits = count, ResetCredits = null }));
        var reserves = rows.Where(r => r.Kind == ResetKind.Reserve).ToArray();
        Assert.Equal(placeholders, reserves.Length);
        Assert.All(reserves, row => Assert.Null(row.At));
    }

    [Fact]
    public void MissingWeeklyWindowNeverUsesAnotherDuration()
    {
        var rows = ResetSchedule.Entries(State(Snapshot with { Buckets = [new("codex", null, [new(20, 300, Now)])] }));
        Assert.Null(Assert.Single(rows, r => r.Kind == ResetKind.Weekly).At);
        Assert.Equal(Now, Assert.Single(rows, r => r.Kind == ResetKind.Short).At);
    }

    [Fact]
    public void AccountFilterIsIsolatedAndDoesNotFallBackToAnotherAccount()
    {
        var other = new AccountProfile(Guid.NewGuid(), "other@example.test");
        var state = new TrackerState([new(Profile, Snapshot), new(other, Snapshot with { Email = other.Email })], Profile.Id);
        var rows = ResetSchedule.Entries(state, other.Id);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Equal(other.Id, row.Account.Profile.Id));
        Assert.Empty(ResetSchedule.Entries(state, Guid.NewGuid()));
        Assert.Empty(ResetSchedule.Entries(new([], null)));
    }

    [Fact]
    public void PartialCreditDetailsKeepKnownDatesAndFlagMissingDates()
    {
        var rows = ResetSchedule.Entries(State(Snapshot with { AvailableResetCredits = 3 }));
        var placeholder = Assert.Single(rows, r => r.IsUndetailedReserve);
        Assert.Null(placeholder.At);
        Assert.Equal(2, rows.Count(r => r.Kind == ResetKind.Reserve && r.At is not null));
    }

    [Fact]
    public void HistoricalCreditDatesDoNotOverrideZeroAvailability()
    {
        var rows = ResetSchedule.Entries(State(Snapshot with { AvailableResetCredits = 0 }));
        Assert.Equal(2, rows.Count(r => r.Kind == ResetKind.Reserve));
        Assert.All(rows, row => Assert.Equal(0, row.Account.Snapshot!.AvailableResetCredits));
    }
}
