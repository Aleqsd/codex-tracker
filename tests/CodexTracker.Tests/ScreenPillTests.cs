using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ScreenPillTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");

    private static AccountState Account(double weeklyLeft, double shortLeft = 100, bool active = true, string plan = "plus",
        ResetCredit[]? credits = null, DateTimeOffset? weekEnds = null, Guid? id = null)
    {
        var accountId = id ?? Guid.NewGuid();
        return new(new(accountId, "work@example.test"), new("work@example.test", plan,
            [new("codex", null, [new(100 - weeklyLeft, 10080, weekEnds ?? Now.AddDays(2)), new(100 - shortLeft, 300, Now.AddHours(2))])],
            credits?.Length ?? 0, credits ?? [], Now.AddMinutes(-2)), IsActiveInCodex: active);
    }
    private static TrackerState State(params AccountState[] accounts) => new(accounts, accounts[0].Profile.Id);

    [Fact]
    public void NothingShowsWhileQuotaIsComfortableAndNoCreditExpiresSoon()
    {
        Assert.Empty(ScreenPill.Current(State(Account(40, credits: [new("c", "Full reset", Now.AddDays(-1), Now.AddDays(3))])), Now));
        Assert.Empty(ScreenPill.Current(State(Account(5, active: false)), Now)); // an inactive account is not "in use"
    }

    [Fact]
    public void ALowWindowOfTheAccountInUseShowsWithItsReset()
    {
        var pill = Assert.Single(ScreenPill.Current(State(Account(70, shortLeft: 4)), Now));
        Assert.Equal(PillKind.LowQuota, pill.Kind);
        Assert.Equal(ResetKind.Short, pill.Window);
        Assert.Equal(4, pill.Remaining!.Value, 3);
        Assert.Equal(Now.AddHours(2), pill.At);
        // Codex Pro has no 5-hour window: only the week counts.
        Assert.Empty(ScreenPill.Current(State(Account(70, shortLeft: 0, plan: "pro")), Now));
    }

    [Fact]
    public void APassedResetOrADeclaredResetIsNotLow()
    {
        Assert.Empty(ScreenPill.Current(State(Account(3, weekEnds: Now.AddMinutes(-5))), Now));
        var declared = State(Account(3)) with { ManualCodexReset = new(Now.AddMinutes(-1), Now.AddMinutes(-1)) };
        Assert.Empty(ScreenPill.Current(declared, Now));
    }

    [Fact]
    public void ACreditExpiringWithin24HoursShowsAfterALowQuota()
    {
        var credit = new ResetCredit("credit-1", "Full reset", Now.AddDays(-5), Now.AddHours(6));
        var both = ScreenPill.Current(State(Account(8, credits: [credit])), Now);
        Assert.Equal([PillKind.LowQuota, PillKind.ExpiringReserve], both.Select(p => p.Kind));
        Assert.Equal(Now.AddHours(6), both[1].At);
        Assert.Empty(ScreenPill.Current(State(Account(50, credits: [credit with { ExpiresAt = Now.AddHours(-1) }])), Now));
    }

    [Fact]
    public void HidingLastsUntilAnotherSituation()
    {
        var id = Guid.NewGuid();
        var first = Assert.Single(ScreenPill.Current(State(Account(6, id: id)), Now));
        Assert.Empty(ScreenPill.Current(State(Account(4, id: id)), Now, [first.Key]));           // same period, lower: still hidden
        var next = Assert.Single(ScreenPill.Current(State(Account(6, id: id, weekEnds: Now.AddDays(9))), Now, [first.Key]));
        Assert.NotEqual(first.Key, next.Key);                                                   // next period shows again
    }
}
