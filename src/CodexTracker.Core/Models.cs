namespace CodexTracker.Core;

public sealed record AccountProfile(Guid Id, string Email);
public sealed record QuotaWindow(double UsedPercent, int? WindowDurationMins, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public bool IsWeekly => WindowDurationMins == 10080;
}
public sealed record QuotaBucket(string Id, string? Name, IReadOnlyList<QuotaWindow> Windows);
public sealed record ResetCredit(string Id, string? Title, DateTimeOffset? GrantedAt, DateTimeOffset? ExpiresAt);
public sealed record AccountSnapshot(string Email, string? PlanType, IReadOnlyList<QuotaBucket> Buckets,
    int? AvailableResetCredits, IReadOnlyList<ResetCredit>? ResetCredits, DateTimeOffset FetchedAt,
    int? PlanMultiplier = null, DateTimeOffset? SubscriptionStartedAt = null, DateTimeOffset? SubscriptionEndsAt = null)
{
    public QuotaWindow? Weekly => Buckets.FirstOrDefault(b => b.Id == "codex")?.Windows
        .Where(w => w.IsWeekly).OrderBy(w => w.RemainingPercent).FirstOrDefault();
    public QuotaWindow? Short => Buckets.FirstOrDefault(b => b.Id == "codex")?.Windows
        .Where(w => w.WindowDurationMins == 300).OrderBy(w => w.RemainingPercent).FirstOrDefault();
}
public sealed record AccountState(AccountProfile Profile, AccountSnapshot? Snapshot = null,
    bool IsActiveInCodex = false, bool IsConnected = false, bool IsRefreshing = false,
    string? Error = null)
{
    public bool IsStale => Error is not null || (Snapshot is not null && (!IsActiveInCodex || DateTimeOffset.UtcNow - Snapshot.FetchedAt > TimeSpan.FromMinutes(5)));
}
public sealed record TrackerState(IReadOnlyList<AccountState> Accounts, Guid? SelectedAccountId,
    bool IsBusy = false, string? StatusMessage = null, bool OnboardingComplete = false)
{
    public AccountState? ActiveAccount => Accounts.FirstOrDefault(a => a.IsActiveInCodex);
    public AccountState? SelectedAccount => ActiveAccount;
}

public interface ITrackerService : IAsyncDisposable
{
    event EventHandler? Changed;
    event EventHandler<QuotaNotification>? Notification;
    TrackerState State { get; }
    IReadOnlyList<UsageSample> GetHistory(Guid accountId);
    UsageForecast GetForecast(Guid accountId);
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task SuspendAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(Guid id, CancellationToken cancellationToken = default);
    Task ImportCurrentAccountAsync(CancellationToken cancellationToken = default);
    Task CompleteOnboardingAsync(CancellationToken cancellationToken = default);
}
