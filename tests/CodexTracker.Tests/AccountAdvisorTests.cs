using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class AccountAdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
    private static readonly Guid ActiveId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ComfortableActiveAccountWinsRegardlessOfSelectionAndOtherPercentages()
    {
        var active = Account(ActiveId, active: true, weekly: 21, shortQuota: 21, plan: "free");
        var other = Account(OtherId, weekly: 100, shortQuota: 100, plan: "pro");
        var advice = AccountAdvisor.Evaluate(new([other, active], OtherId), Now);
        Assert.Equal(ActiveId, advice.AccountId);
        Assert.Equal(AccountAdviceKind.CurrentComfortable, advice.Kind);
        Assert.Equal(AccountAdviceConfidence.RecentObservation, advice.Confidence);
        Assert.Equal(active.Snapshot!.FetchedAt, advice.ObservedAt);
    }

    [Fact]
    public void AQuotaJustAboveTheThresholdIsNotRoundedDownForAdvice()
    {
        Assert.Equal(AccountAdviceKind.CurrentComfortable,
            Evaluate(Account(ActiveId, active: true, weekly: 20.000001, shortQuota: 20.000001), Account(OtherId)).Kind);
    }

    [Theory]
    [InlineData(20, 90)]
    [InlineData(90, 20)]
    [InlineData(0, 0)]
    public void EitherLowWindowPermitsOnlyAHistoricalSuggestion(double weekly, double shortQuota)
    {
        var other = Account(OtherId, age: TimeSpan.FromMinutes(17));
        var advice = Evaluate(Account(ActiveId, active: true, weekly: weekly, shortQuota: shortQuota), other);
        Assert.Equal(OtherId, advice.AccountId);
        Assert.Equal(AccountAdviceKind.VerifyInCodex, advice.Kind);
        Assert.Equal(AccountAdviceConfidence.HistoricalObservation, advice.Confidence);
        Assert.Equal("À vérifier dans Codex", advice.Label);
        Assert.Equal(other.Snapshot!.FetchedAt, advice.ObservedAt);
        Assert.Equal(other.Snapshot.Short!.ResetsAt, advice.NextResetAt);
        Assert.Contains("25/10/2026 01:13:00 UTC+00:00", advice.Reason);
        Assert.Contains("actuels restent à vérifier", advice.Reason);
        Assert.DoesNotContain("@", advice.Reason);
    }

    [Fact]
    public void MoreRecentQualifiedObservationWinsWithoutComparingSubscriptionCapacity()
    {
        var active = Account(ActiveId, active: true, weekly: 10, plan: "pro");
        var older = Account(OtherId, age: TimeSpan.FromHours(1), weekly: 100, shortQuota: 100, plan: "pro");
        var recent = Account(ThirdId, age: TimeSpan.FromMinutes(10), weekly: 21, shortQuota: 21, plan: null);
        var advice = Evaluate(active, older, recent);
        Assert.Equal(ThirdId, advice.AccountId);
        Assert.Contains("capacités des offres ne sont pas comparées", advice.Reason);
        Assert.Equal(ThirdId, Evaluate(active, older with { Snapshot = older.Snapshot! with { PlanType = "free" } },
            recent with { Snapshot = recent.Snapshot! with { PlanType = "pro", PlanMultiplier = 20 } }).AccountId);
    }

    [Fact]
    public void EqualTimestampsUseAStableTieBreakInsteadOfAccountListOrder()
    {
        var active = Account(ActiveId, active: true, weekly: 10);
        var other = Account(OtherId);
        var third = Account(ThirdId);
        Assert.Equal(OtherId, Evaluate(active, third, other).AccountId);
        Assert.Equal(OtherId, Evaluate(other, active, third).AccountId);
    }

    [Theory]
    [InlineData(20, 90)]
    [InlineData(90, 20)]
    [InlineData(0, 0)]
    public void InactiveAccountWithAnyLowWindowIsNotSuggested(double weekly, double shortQuota)
    {
        var advice = Evaluate(Account(ActiveId, active: true, weekly: 10), Account(OtherId, weekly: weekly, shortQuota: shortQuota));
        AssertUnavailable(advice);
        Assert.Contains("Aucun autre relevé", advice.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingEitherWindowIsUnknownAndNeverMeansZeroOrAvailable(bool omitWeekly)
    {
        var active = Account(ActiveId, active: true, weekly: 10);
        var incomplete = WithoutWindow(Account(OtherId), omitWeekly ? 10080 : 300);
        AssertUnavailable(Evaluate(active, incomplete));
        var incompleteActive = WithoutWindow(active, omitWeekly ? 10080 : 300);
        var advice = Evaluate(incompleteActive, Account(OtherId));
        AssertUnavailable(advice);
        Assert.Contains("doivent être connus", advice.Reason);
    }

    [Fact]
    public void MissingResetOrReachedDeadlineRequiresAConfirmedNewObservation()
    {
        var active = Account(ActiveId, active: true, weekly: 10);
        var other = Account(OtherId);
        foreach (var reset in new DateTimeOffset?[] { null, Now.AddTicks(-1), Now })
        {
            AssertUnavailable(Evaluate(active, ChangeWindow(other, 300, w => w with { ResetsAt = reset })));
            AssertUnavailable(Evaluate(ChangeWindow(active, 10080, w => w with { ResetsAt = reset }), other));
        }
        var nextTick = ChangeWindow(other, 300, w => w with { ResetsAt = Now.AddTicks(1) });
        Assert.Equal(OtherId, Evaluate(active, nextTick).AccountId);
        AssertUnavailable(AccountAdvisor.Evaluate(new([active, nextTick], ActiveId), Now.AddTicks(1)));
    }

    [Fact]
    public void ResetDeadlinePassedDoesNotAssumeThatAnEmptyQuotaRefilled()
    {
        var empty = Account(ActiveId, active: true, weekly: 0);
        empty = ChangeWindow(empty, 10080, w => w with { ResetsAt = Now });
        var advice = Evaluate(empty, Account(OtherId));
        AssertUnavailable(advice);
        Assert.Contains("aucun rechargement n’est supposé", advice.Reason);
    }

    [Fact]
    public void ActiveAndInactiveFreshnessLimitsAreInclusiveToTheTick()
    {
        var active = Account(ActiveId, active: true, weekly: 10, age: AccountAdvisor.MaximumActiveAge);
        var other = Account(OtherId, age: AccountAdvisor.MaximumInactiveAge);
        Assert.Equal(OtherId, Evaluate(active, other).AccountId);
        AssertUnavailable(Evaluate(active with { Snapshot = active.Snapshot! with { FetchedAt = active.Snapshot.FetchedAt.AddTicks(-1) } }, other));
        AssertUnavailable(Evaluate(active, other with { Snapshot = other.Snapshot! with { FetchedAt = other.Snapshot.FetchedAt.AddTicks(-1) } }));
    }

    [Fact]
    public void FutureTimestampsCannotProduceAdvice()
    {
        var active = Account(ActiveId, active: true, weekly: 10);
        var future = Account(OtherId, age: TimeSpan.FromTicks(-1));
        AssertUnavailable(Evaluate(active, future));
        AssertUnavailable(Evaluate(active with { Snapshot = active.Snapshot! with { FetchedAt = Now.AddTicks(1) } }, Account(OtherId)));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("disconnected")]
    [InlineData("refreshing")]
    [InlineData("missing")]
    [InlineData("wrong-email")]
    public void UncertainOrExpiredSessionAndMismatchedIdentityAreExcluded(string condition)
    {
        var active = Account(ActiveId, active: true, weekly: 10);
        var other = Account(OtherId);
        AccountState Invalid(AccountState value) => condition switch
        {
            "error" => value with { Error = "Session expirée" },
            "disconnected" => value with { IsConnected = false },
            "refreshing" => value with { IsRefreshing = true },
            "missing" => value with { Snapshot = null },
            _ => value with { Snapshot = value.Snapshot! with { Email = "different@example.test" } }
        };
        AssertUnavailable(Evaluate(active, Invalid(other)));
        AssertUnavailable(Evaluate(Invalid(active), other));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidRawPercentagesCannotBecomeComfortableByClamping(double used)
    {
        var active = Account(ActiveId, active: true, weekly: 10);
        var invalid = ChangeWindow(Account(OtherId), 300, w => w with { UsedPercent = used });
        AssertUnavailable(Evaluate(active, invalid));
        AssertUnavailable(Evaluate(ChangeWindow(active, 300, w => w with { UsedPercent = used }), Account(OtherId)));
    }

    [Fact]
    public void UnknownOrConflictingActiveIdentityDoesNotSuggestAnotherAccount()
    {
        AssertUnavailable(Evaluate(Account(OtherId)));
        AssertUnavailable(Evaluate(Account(ActiveId, active: true), Account(OtherId, active: true)));
        AssertUnavailable(Evaluate(Account(ActiveId, active: true), Account(ActiveId)));
        AssertUnavailable(Evaluate(Account(Guid.Empty, active: true)));
        var active = Account(ActiveId, active: true, weekly: 10);
        AssertUnavailable(Evaluate(active, Account(OtherId) with { Profile = new(OtherId, active.Profile.Email.ToUpperInvariant()) }));
    }

    [Fact]
    public void SubscriptionPeriodEndIsNotMistakenForSessionExpiry()
    {
        var other = Account(OtherId);
        other = other with { Snapshot = other.Snapshot! with { SubscriptionEndsAt = Now.AddDays(-1) } };
        Assert.Equal(OtherId, Evaluate(Account(ActiveId, active: true, weekly: 10), other).AccountId);
    }

    [Fact]
    public void TimezoneOffsetsAndDstDoNotChangeFreshnessOrDeadlineDecisions()
    {
        var active = Account(ActiveId, active: true, weekly: 10);
        var other = Account(OtherId, age: TimeSpan.FromMinutes(30));
        other = other with { Snapshot = other.Snapshot! with { FetchedAt = other.Snapshot.FetchedAt.ToOffset(TimeSpan.FromHours(2)),
            Email = other.Profile.Email.ToUpperInvariant() } };
        other = ChangeWindow(other, 300, w => w with { ResetsAt = Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(1)) });
        var advice = AccountAdvisor.Evaluate(new([active, other], null), Now.ToOffset(TimeSpan.FromHours(1)));
        Assert.Equal(OtherId, advice.AccountId);
        Assert.Contains("25/10/2026 03:00:00 UTC+02:00", advice.Reason);
        Assert.Equal(Now.AddMinutes(1), advice.NextResetAt);
    }

    private static AccountAdvice Evaluate(params AccountState[] accounts) => AccountAdvisor.Evaluate(new(accounts, null), Now);

    private static void AssertUnavailable(AccountAdvice advice)
    {
        Assert.Equal(AccountAdviceKind.InsufficientData, advice.Kind);
        Assert.Equal(AccountAdviceConfidence.InsufficientData, advice.Confidence);
        Assert.Null(advice.AccountId);
        Assert.False(string.IsNullOrWhiteSpace(advice.Reason));
    }

    private static AccountState Account(Guid id, bool active = false, double weekly = 70, double shortQuota = 80,
        TimeSpan? age = null, string? plan = "plus")
    {
        var email = $"account-{id:N}@example.test";
        return new(new(id, email), new(email, plan,
            [new("codex", "Codex", [new(100 - weekly, 10080, Now.AddDays(3)), new(100 - shortQuota, 300, Now.AddHours(2))])],
            null, null, Now - (age ?? TimeSpan.FromMinutes(1))), IsActiveInCodex: active, IsConnected: true);
    }

    private static AccountState WithoutWindow(AccountState account, int minutes) => account with
    {
        Snapshot = account.Snapshot! with { Buckets = [new("codex", "Codex", account.Snapshot.Buckets[0].Windows.Where(w => w.WindowDurationMins != minutes).ToArray())] }
    };

    private static AccountState ChangeWindow(AccountState account, int minutes, Func<QuotaWindow, QuotaWindow> change) => account with
    {
        Snapshot = account.Snapshot! with { Buckets = [new("codex", "Codex", account.Snapshot.Buckets[0].Windows.Select(w => w.WindowDurationMins == minutes ? change(w) : w).ToArray())] }
    };
}
