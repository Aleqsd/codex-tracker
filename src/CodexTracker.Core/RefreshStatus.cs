namespace CodexTracker.Core;

public enum RefreshOutcome { Unavailable, Refreshing, Updated, Unchanged, Failed, Suspended }
public enum RefreshReason { Startup, Manual, SourceChanged, Timer, Retry, Resume }

/// <summary>Collection metadata, distinct from the original timestamp of a measured quota.</summary>
public sealed record ProviderRefreshStatus(AccountProvider Provider, Guid? AccountId, RefreshOutcome Outcome,
    DateTimeOffset? AttemptedAt = null, DateTimeOffset? CompletedAt = null, DateTimeOffset? ObservedAt = null,
    double? DurationMilliseconds = null, DateTimeOffset? NextAttemptAt = null,
    RefreshReason Reason = RefreshReason.Startup, int ConsecutiveFailures = 0, string? ErrorCode = null);

public static class SnapshotContent
{
    public static bool Equal(AccountSnapshot? first, AccountSnapshot? second) => ReferenceEquals(first, second) ||
        first is not null && second is not null &&
        first with { Buckets = second.Buckets, ResetCredits = second.ResetCredits } == second &&
        first.Buckets.Count == second.Buckets.Count && first.Buckets.Zip(second.Buckets).All(pair =>
            pair.First.Id == pair.Second.Id && pair.First.Name == pair.Second.Name && pair.First.Windows.SequenceEqual(pair.Second.Windows)) &&
        (first.ResetCredits is null ? second.ResetCredits is null : second.ResetCredits is not null && first.ResetCredits.SequenceEqual(second.ResetCredits));
}
