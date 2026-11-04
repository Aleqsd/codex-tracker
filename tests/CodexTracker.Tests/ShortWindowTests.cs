using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ShortWindowTests
{
    [Theory]
    [InlineData(AccountProvider.Codex, "pro", null, false)]
    [InlineData(AccountProvider.Codex, "ProLite", null, false)]
    [InlineData(AccountProvider.Codex, null, "pro", false)]
    [InlineData(AccountProvider.Codex, "plus", "pro", true)]
    [InlineData(AccountProvider.Codex, "plus", null, true)]
    [InlineData(AccountProvider.Codex, null, null, true)]
    [InlineData(AccountProvider.ClaudeCode, "pro", null, true)]
    public void OnlyCodexProHidesTheFiveHourQuota(AccountProvider provider, string? snapshotPlan, string? profilePlan, bool shown)
    {
        var profile = new AccountProfile(Guid.NewGuid(), "plan@example.test", provider, ProviderPlanType: profilePlan);
        var snapshot = snapshotPlan is null ? null : new AccountSnapshot("plan@example.test", snapshotPlan, [], null, null, DateTimeOffset.UtcNow);
        Assert.Equal(shown, new AccountState(profile, snapshot).HasShortWindow);
    }

    [Theory]
    // A reading with the week and no 5-hour window: the plan has none (Claude Ultra, a lifted 5-hour limit).
    [InlineData(AccountProvider.ClaudeCode, "max", true, false, false)]
    [InlineData(AccountProvider.Codex, "plus", true, false, false)]
    [InlineData(AccountProvider.ClaudeCode, "max", true, true, true)]
    [InlineData(AccountProvider.Codex, "plus", true, true, true)]
    // A reading without the week does not tell: the 5-hour quota stays offered.
    [InlineData(AccountProvider.ClaudeCode, "pro", false, true, true)]
    [InlineData(AccountProvider.Codex, "plus", false, false, true)]
    // Codex Pro never shows one, even if a 5-hour window is received.
    [InlineData(AccountProvider.Codex, "pro", true, true, false)]
    public void AReadingWithTheWeekOnlyHidesTheFiveHourQuota(AccountProvider provider, string plan, bool weekly, bool fiveHours, bool shown)
    {
        var now = DateTimeOffset.UtcNow;
        var windows = new List<QuotaWindow>();
        if (fiveHours) windows.Add(new(30, 300, now.AddHours(2)));
        if (weekly) windows.Add(new(40, 10080, now.AddDays(3)));
        var bucket = provider == AccountProvider.ClaudeCode ? "claude" : "codex";
        var profile = new AccountProfile(Guid.NewGuid(), "plan@example.test", provider);
        var snapshot = new AccountSnapshot("plan@example.test", plan, [new(bucket, null, windows)], null, null, now);
        Assert.Equal(shown, new AccountState(profile, snapshot).HasShortWindow);
    }

    [Fact]
    public void TheFiveHourQuotaReturnsWithTheNextReadingThatHasOne()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AccountProfile(Guid.NewGuid(), "plan@example.test", AccountProvider.ClaudeCode);
        var weekOnly = new AccountState(profile, new("plan@example.test", "max", [new("claude", null, [new(40, 10080, now.AddDays(3))])], null, null, now));
        Assert.False(weekOnly.HasShortWindow);
        var both = weekOnly with { Snapshot = weekOnly.Snapshot! with { Buckets = [new("claude", null, [new(10, 300, now.AddHours(4)), new(41, 10080, now.AddDays(3))])] } };
        Assert.True(both.HasShortWindow);
    }

    [Fact]
    public void ADeclaredCodexResetProjectsNoFiveHourResetForAWeekOnlyReading()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AccountProfile(Guid.NewGuid(), "plan@example.test");
        var account = new AccountState(profile, new("plan@example.test", "plus", [new("codex", null, [new(40, 10080, now.AddDays(3))])], null, null, now.AddHours(-2)));
        var declared = new ManualCodexReset(now.AddHours(-1), now.AddHours(-1));
        Assert.NotNull(declared.ProjectedReset(account, ResetKind.Weekly, now));
        Assert.Null(declared.ProjectedReset(account, ResetKind.Short, now));
    }
}
