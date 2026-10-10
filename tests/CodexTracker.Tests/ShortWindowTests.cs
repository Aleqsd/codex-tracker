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
}
