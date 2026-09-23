using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ExpectedResetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-25T01:30:00Z");
    private static AccountState Account(int minutes = 10080, DateTimeOffset? reset = null, double used = 92) => new(
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "studio@example.test"),
        new("studio@example.test", "plus", [new("codex", null, [new(used, minutes, reset ?? Now)])], 2, [], Now.AddDays(-1)));

    [Fact]
    public void CrossingTheExactInstantEstimatesWithoutChangingObservation()
    {
        var account = Account();
        Assert.Null(ExpectedReset.For(account, ResetKind.Weekly, Now.AddTicks(-1)));
        var reset = Assert.IsType<ExpectedReset>(ExpectedReset.For(account, ResetKind.Weekly, Now));
        Assert.Equal(8, reset.LastRemainingPercent);
        Assert.Equal(8, account.Snapshot!.Weekly!.RemainingPercent);
        Assert.Equal(Now.AddDays(-1), account.Snapshot.FetchedAt);
        Assert.Equal(Now, reset.At);
    }

    [Fact]
    public void ActiveAccountsAndNewerObservationsNeverEstimate()
    {
        var account = Account();
        Assert.Null(ExpectedReset.For(account with { IsActiveInCodex = true }, ResetKind.Weekly, Now));
        Assert.Null(ExpectedReset.For(account with { IsRefreshing = true }, ResetKind.Weekly, Now));
        Assert.Null(ExpectedReset.For(account with { Snapshot = account.Snapshot! with { FetchedAt = Now } }, ResetKind.Weekly, Now));
        Assert.Null(ExpectedReset.For(account with { Snapshot = account.Snapshot! with { FetchedAt = Now.AddMinutes(1) } }, ResetKind.Weekly, Now));
    }

    [Fact]
    public void MissingOrMismatchedDataAndReservesNeverEstimate()
    {
        var account = Account();
        Assert.Null(ExpectedReset.For(account with { Snapshot = null }, ResetKind.Weekly, Now));
        Assert.Null(ExpectedReset.For(account with { Snapshot = account.Snapshot! with { Email = "other@example.test" } }, ResetKind.Weekly, Now));
        Assert.Null(ExpectedReset.For(account with { Snapshot = account.Snapshot! with { Buckets = [new("codex", null, [new(92, 10080, null)])] } }, ResetKind.Weekly, Now));
        Assert.Null(ExpectedReset.For(Account(300), ResetKind.Weekly, Now));
        Assert.Null(ExpectedReset.For(account, ResetKind.Reserve, Now));
        Assert.Null(ExpectedReset.For(Account(10081), ResetKind.Weekly, Now));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)] [InlineData(101)]
    public void InvalidQuotasNeverEstimate(double used) => Assert.Null(ExpectedReset.For(Account(used: used), ResetKind.Weekly, Now));

    [Theory]
    [InlineData(ResetKind.Weekly, 10080)] [InlineData(ResetKind.Short, 300)]
    public void EstimateExpiresAfterOneCycleWithoutInventingTheNext(ResetKind kind, int minutes)
    {
        Assert.NotNull(ExpectedReset.For(Account(minutes), kind, Now.AddMinutes(minutes).AddTicks(-1)));
        Assert.Null(ExpectedReset.For(Account(minutes), kind, Now.AddMinutes(minutes)));
    }

    [Fact]
    public void CatchupIsBoundedAndIdentityIsStableAcrossClockChanges()
    {
        var account = Account(reset: Now.ToOffset(TimeSpan.FromHours(2)));
        var state = new TrackerState([account], null);
        var before = Assert.Single(ExpectedReset.Due(state, Now));
        var after = Assert.Single(ExpectedReset.Due(state, Now.AddHours(23)));
        Assert.Equal(before.Key, after.Key);
        Assert.Empty(ExpectedReset.Due(state, Now.AddHours(24)));
        Assert.Equal(ReminderChannel.Windows, before.Channel);
        Assert.Contains("probablement", ReminderPlanner.Body(before));
        Assert.Contains("confirmer", ReminderPlanner.Body(before));
        Assert.DoesNotContain("confirmé", ReminderPlanner.Body(before));
    }

    [Fact]
    public void NotificationIdentitySeparatesAccountsKindsAndChangedDeadlines()
    {
        var account = Account();
        var other = account with { Profile = new(Guid.NewGuid(), "other@example.test"), Snapshot = account.Snapshot! with { Email = "other@example.test" } };
        Assert.Equal(2, ExpectedReset.Due(new([account, other], null), Now).Select(r => r.Key).Distinct().Count());
        string Key(AccountState a) => Assert.Single(ExpectedReset.Due(new([a], null), Now)).Key;
        Assert.NotEqual(Key(account), Key(Account(300)));
        Assert.NotEqual(Key(account), Key(Account(reset: Now.AddMinutes(-1))));
    }
}
