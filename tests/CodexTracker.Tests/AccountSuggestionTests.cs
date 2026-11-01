using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class AccountSuggestionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");

    private static AccountState Account(string email, double weeklyLeft, DateTimeOffset? weekEnds = null, bool active = false,
        AccountProvider provider = AccountProvider.Codex, double shortLeft = 100, string plan = "plus", TimeSpan? age = null)
    {
        var id = Guid.NewGuid();
        var bucket = provider == AccountProvider.ClaudeCode ? "claude" : "codex";
        return new(new(id, email, provider), new(email, plan,
            [new(bucket, null, [new(100 - weeklyLeft, 10080, weekEnds ?? Now.AddDays(3)), new(100 - shortLeft, 300, Now.AddHours(3))])],
            0, [], Now - (age ?? TimeSpan.FromHours(5))), IsActiveInCodex: active && provider == AccountProvider.Codex,
            IsActiveInClaudeCode: active && provider == AccountProvider.ClaudeCode);
    }

    private static TrackerState State(params AccountState[] accounts) => new(accounts, accounts[0].Profile.Id);

    [Fact]
    public void ANearlyEmptyAccountPointsToTheFullestOtherAccountOfTheSameTool()
    {
        var current = Account("work@example.test", 8, active: true);
        var half = Account("perso@example.test", 55);
        var best = Account("team@example.test", 80);
        var claude = Account("claude@example.test", 100, provider: AccountProvider.ClaudeCode);
        var suggestion = AccountSuggestions.For(State(current, half, best, claude), current, Now);
        Assert.NotNull(suggestion);
        Assert.Equal(best.Profile.Id, suggestion.Account.Profile.Id);
        Assert.Equal(80, suggestion.Remaining, 3);
        Assert.False(suggestion.Estimated);
    }

    [Theory]
    [InlineData(15, 80)]  // the current account is not low yet
    [InlineData(8, 25)]   // the other has too little left
    [InlineData(10, 29)]  // below the minimum
    public void NoSuggestionWithoutAClearGain(double currentLeft, double otherLeft)
    {
        var current = Account("work@example.test", currentLeft, active: true);
        Assert.Null(AccountSuggestions.For(State(current, Account("perso@example.test", otherLeft)), current, Now));
    }

    [Fact]
    public void TheShortWindowCountsWhenItIsTheLimit()
    {
        var current = Account("work@example.test", 70, active: true, shortLeft: 4);
        var other = Account("perso@example.test", 90, shortLeft: 3);
        var fresh = Account("team@example.test", 60, shortLeft: 100);
        var suggestion = AccountSuggestions.For(State(current, other, fresh), current, Now);
        Assert.Equal(fresh.Profile.Id, suggestion?.Account.Profile.Id);
        Assert.Equal(60, suggestion!.Remaining, 3);
    }

    [Fact]
    public void ProWithoutAFiveHourWindowUsesTheWeekOnly()
    {
        var current = Account("work@example.test", 5, active: true);
        var pro = Account("pro@example.test", 70, plan: "pro", shortLeft: 0);
        Assert.Equal(pro.Profile.Id, AccountSuggestions.For(State(current, pro), current, Now)?.Account.Profile.Id);
    }

    [Fact]
    public void APassedResetCountsAsFullButEstimatedAndMeasuredQuotaComesFirst()
    {
        var current = Account("work@example.test", 6, active: true);
        var refilled = Account("old@example.test", 2, weekEnds: Now.AddHours(-2), age: TimeSpan.FromDays(6));
        Assert.True(AccountSuggestions.For(State(current, refilled), current, Now)?.Estimated);
        var measured = Account("perso@example.test", 70);
        var suggestion = AccountSuggestions.For(State(current, refilled, measured), current, Now);
        Assert.Equal(measured.Profile.Id, suggestion?.Account.Profile.Id);
        Assert.False(suggestion!.Estimated);
    }

    [Fact]
    public void AnAccountWithoutReadingIsNeverSuggested()
    {
        var current = Account("work@example.test", 3, active: true);
        var empty = new AccountState(new(Guid.NewGuid(), "new@example.test"));
        Assert.Null(AccountSuggestions.For(State(current, empty), current, Now));
    }

    [Fact]
    public void TheLowQuotaAlertNamesTheAccountToSwitchTo()
    {
        var current = Account("work@example.test", 8, active: true);
        var other = Account("perso@example.test", 72);
        var state = State(current, other);
        var alert = new QuotaNotification(current.Profile.Id, current.Profile.Email, NotificationKind.Threshold, 10, Now);
        var (_, body) = CodexTracker.App.NotificationPolicy.Compose(alert, state, now: Now);
        Assert.Contains("Passez sur", body);
        Assert.Contains("72 %", body);
        var reset = alert with { Kind = NotificationKind.Reset, Threshold = null };
        Assert.DoesNotContain("Passez sur", CodexTracker.App.NotificationPolicy.Compose(reset, state, now: Now).Body);
    }
}