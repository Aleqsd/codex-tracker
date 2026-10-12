using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class QuotaPaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-12T12:00:00Z");

    private static AccountState Active(double weeklyUsed, TimeSpan weeklyLeft, double shortUsed = 40, TimeSpan? shortLeft = null, string plan = "plus",
        AccountProvider provider = AccountProvider.Codex) => new(
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "pace@example.test", provider),
        new("pace@example.test", plan, [new(provider == AccountProvider.Codex ? "codex" : "claude", null,
            [new(weeklyUsed, 10080, Now + weeklyLeft), new(shortUsed, 300, Now + (shortLeft ?? TimeSpan.FromHours(2)))])], null, null, Now.AddMinutes(-1)),
        IsActiveInCodex: provider == AccountProvider.Codex, IsActiveInClaudeCode: provider == AccountProvider.ClaudeCode);

    private static QuotaPace Pace(AccountState account, ResetKind kind = ResetKind.Weekly) =>
        Assert.IsType<QuotaPace>(QuotaPace.For(new([account], null), account, kind, Now));

    [Fact]
    public void SteadyLineFollowsTheTimeLeftInTheWindow()
    {
        // Two days of seven left: a steady use leaves 2/7 of the week, i.e. 28.6 %.
        var ahead = Pace(Active(weeklyUsed: 50, TimeSpan.FromDays(2)));
        Assert.Equal(200d / 7, ahead.Expected, 6);
        Assert.Equal(50 - 200d / 7, ahead.Margin, 6);
        Assert.False(ahead.OnPace);
        Assert.Equal((25d, false), ahead.Budget);

        var behind = Pace(Active(weeklyUsed: 80, TimeSpan.FromDays(3.5)));
        Assert.Equal(50, behind.Expected, 6);
        Assert.Equal(-30, behind.Margin, 6);

        // The 5-hour window uses its own duration: two hours of five left.
        var hourly = Pace(Active(weeklyUsed: 50, TimeSpan.FromDays(2), shortUsed: 60), ResetKind.Short);
        Assert.Equal(40, hourly.Expected, 6);
        Assert.True(hourly.OnPace);
        Assert.Equal((20d, true), hourly.Budget);
    }

    [Fact]
    public void ToleranceAndBudgetLimitsAreExact()
    {
        // 3.5 days left: steady 50 %. Within three points is on pace; three points or more is not.
        Assert.True(Pace(Active(weeklyUsed: 47.01, TimeSpan.FromDays(3.5))).OnPace);
        Assert.False(Pace(Active(weeklyUsed: 47, TimeSpan.FromDays(3.5))).OnPace);
        Assert.False(Pace(Active(weeklyUsed: 53, TimeSpan.FromDays(3.5))).OnPace);
        // From one day left, the budget is given per hour; under ten minutes it is no longer given.
        var lastDay = Pace(Active(weeklyUsed: 80, TimeSpan.FromHours(23))).Budget!.Value;
        Assert.True(lastDay.PerHour);
        Assert.Equal(20d / 23, lastDay.Amount, 6);
        Assert.True(Pace(Active(weeklyUsed: 80, TimeSpan.FromDays(1) - TimeSpan.FromTicks(1))).Budget!.Value.PerHour);
        Assert.False(Pace(Active(weeklyUsed: 80, TimeSpan.FromDays(1))).Budget!.Value.PerHour);
        Assert.NotNull(Pace(Active(weeklyUsed: 80, TimeSpan.FromMinutes(10))).Budget);
        Assert.Null(Pace(Active(weeklyUsed: 80, TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1))).Budget);
        // A full window just after its reset is exactly on the line.
        Assert.Equal(0, Pace(Active(weeklyUsed: 0, TimeSpan.FromDays(7))).Margin, 6);
    }

    [Fact]
    public void NoPaceIsInventedWithoutAMeasuredWindowOfTheActiveAccount()
    {
        var account = Active(weeklyUsed: 50, TimeSpan.FromDays(2));
        var state = new TrackerState([account], null);
        Assert.Null(QuotaPace.For(state, account with { IsActiveInCodex = false }, ResetKind.Weekly, Now));
        Assert.Null(QuotaPace.For(state, account with { Snapshot = null }, ResetKind.Weekly, Now));
        Assert.Null(QuotaPace.For(state, account, ResetKind.Reserve, Now));
        // Reached or inconsistent dates.
        Assert.Null(QuotaPace.For(state, account, ResetKind.Weekly, Now.AddDays(2)));
        Assert.Null(QuotaPace.For(state, Active(weeklyUsed: 50, TimeSpan.FromDays(7) + TimeSpan.FromTicks(1)), ResetKind.Weekly, Now));
        // Unknown date or duration, inferred Claude date.
        var buckets = account.Snapshot!.Buckets;
        var noDate = account with { Snapshot = account.Snapshot with { Buckets = [buckets[0] with { Windows = [buckets[0].Windows[0] with { ResetsAt = null }] }] } };
        Assert.Null(QuotaPace.For(state, noDate, ResetKind.Weekly, Now));
        var inferred = account with { Snapshot = account.Snapshot with { Buckets = [buckets[0] with { Windows = [buckets[0].Windows[0] with { EstimatedResetFrom = Now }] }] } };
        Assert.Null(QuotaPace.For(state, inferred, ResetKind.Weekly, Now));
        // Codex Pro has no 5-hour quota; a declared Codex reset restarts the windows without a measure.
        Assert.Null(QuotaPace.For(state, Active(weeklyUsed: 50, TimeSpan.FromDays(2), plan: "pro"), ResetKind.Short, Now));
        Assert.NotNull(QuotaPace.For(state, Active(weeklyUsed: 50, TimeSpan.FromDays(2), plan: "pro"), ResetKind.Weekly, Now));
        // The declaration follows the last reading (one minute ago), as when OpenAI resets everyone.
        var declared = state with { ManualCodexReset = new(Now.AddSeconds(-30), Now.AddSeconds(-10)) };
        Assert.Null(QuotaPace.For(declared, account, ResetKind.Weekly, Now));
        var claude = Active(weeklyUsed: 50, TimeSpan.FromDays(2), provider: AccountProvider.ClaudeCode);
        Assert.NotNull(QuotaPace.For(declared, claude, ResetKind.Weekly, Now));
    }
}
