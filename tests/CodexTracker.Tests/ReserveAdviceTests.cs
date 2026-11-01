using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ReserveAdviceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");
    private static readonly Guid AccountId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static TrackerState State(double weeklyLeft, DateTimeOffset weekEnds, DateTimeOffset? expires, TimeSpan? age = null, int? available = 1,
        AccountProvider provider = AccountProvider.Codex, DateTimeOffset? estimatedFrom = null) => new([
        new(new(AccountId, "studio@example.test", provider), new("studio@example.test", "plus",
            [new("codex", null, [new(100 - weeklyLeft, 10080, weekEnds, estimatedFrom)])], available,
            expires is { } at ? [new("credit-1", "Full reset", Now.AddDays(-20), at)] : [], Now - (age ?? TimeSpan.FromMinutes(2))), IsActiveInCodex: true)], AccountId);

    [Fact]
    public void ALowWeekWithACreditExpiringBeforeTheRenewalAdvisesItOnce()
    {
        var due = Assert.Single(ReserveAdvice.Due(State(8, Now.AddDays(3), Now.AddDays(1)), Now));
        Assert.True(ReserveAdvice.Is(due));
        Assert.Equal("credit-1", due.CreditId);
        Assert.Equal("Utilisez un reset en réserve", ReminderPlanner.Title(due));
        Assert.Contains("utilisez-le maintenant", ReminderPlanner.Body(due));
        Assert.Equal(ReminderPlanner.Title(due), DesktopReminderText.For([due]).Title);
    }

    [Theory]
    [InlineData(20, 1, 3, 2, 1)]      // enough quota left
    [InlineData(8, 5, 3, 2, 1)]       // the credit outlives the week: keep it
    [InlineData(8, 1, 3, 20 * 60, 1)] // reading too old to know the week
    [InlineData(8, 1, 3, 2, 0)]       // no credit available any more
    [InlineData(8, -1, 3, 2, 1)]      // credit already expired
    public void NoAdviceWithoutAllConditions(double left, int expiresInDays, int weekEndsInDays, int ageMinutes, int available) =>
        Assert.Empty(ReserveAdvice.Due(State(left, Now.AddDays(weekEndsInDays), Now.AddDays(expiresInDays), TimeSpan.FromMinutes(ageMinutes), available), Now));

    [Fact]
    public void EstimatedWeeksDeclaredResetsAndClaudeAccountsGetNoAdvice()
    {
        Assert.Empty(ReserveAdvice.Due(State(8, Now.AddDays(3), Now.AddDays(1), estimatedFrom: Now.AddDays(2)), Now));
        Assert.Empty(ReserveAdvice.Due(State(8, Now.AddDays(3), Now.AddDays(1), provider: AccountProvider.ClaudeCode), Now));
        // A declaration after the last reading restores the week: the credit can wait.
        var declared = State(8, Now.AddDays(3), Now.AddDays(1), TimeSpan.FromHours(1)) with { ManualCodexReset = new(Now.AddMinutes(-30), Now.AddMinutes(-20)) };
        Assert.Empty(ReserveAdvice.Due(declared, Now));
    }
}
