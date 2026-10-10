namespace CodexTracker.Core;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AccountProvider>))]
public enum AccountProvider { Codex, ClaudeCode }
public sealed record AccountProfile(Guid Id, string Email, AccountProvider Provider = AccountProvider.Codex,
    string? ProviderAccountId = null, string? OrganizationName = null, string? ProviderPlanType = null, int? ProviderPlanMultiplier = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProviderName => Provider == AccountProvider.ClaudeCode ? "Claude Code" : "Codex";
    [System.Text.Json.Serialization.JsonIgnore]
    public string IdentityKey => Provider + "\n" + Email.Trim().ToUpperInvariant() +
        (Provider == AccountProvider.ClaudeCode ? "\n" + ProviderAccountId : "");
}
/// <param name="EstimatedResetFrom">Set only for an inferred reset: the reset is expected between this instant and <paramref name="ResetsAt"/>.</param>
public sealed record QuotaWindow(double UsedPercent, int? WindowDurationMins, DateTimeOffset? ResetsAt, DateTimeOffset? EstimatedResetFrom = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool IsResetEstimated => EstimatedResetFrom is not null && ResetsAt is not null;
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public bool IsWeekly => WindowDurationMins == 10080;
}
public sealed record QuotaBucket(string Id, string? Name, IReadOnlyList<QuotaWindow> Windows);
public sealed record ResetCredit(string Id, string? Title, DateTimeOffset? GrantedAt, DateTimeOffset? ExpiresAt);
public sealed record AccountSnapshot(string Email, string? PlanType, IReadOnlyList<QuotaBucket> Buckets,
    int? AvailableResetCredits, IReadOnlyList<ResetCredit>? ResetCredits, DateTimeOffset FetchedAt,
    int? PlanMultiplier = null, DateTimeOffset? SubscriptionStartedAt = null, DateTimeOffset? SubscriptionEndsAt = null)
{
    public QuotaWindow? Weekly => Buckets.FirstOrDefault(b => b.Id is "codex" or "claude")?.Windows
        .Where(w => w.IsWeekly).OrderBy(w => w.RemainingPercent).FirstOrDefault();
    public QuotaWindow? Short => Buckets.FirstOrDefault(b => b.Id is "codex" or "claude")?.Windows
        .Where(w => w.WindowDurationMins == 300).OrderBy(w => w.RemainingPercent).FirstOrDefault();
}
public sealed record AccountState(AccountProfile Profile, AccountSnapshot? Snapshot = null,
    bool IsActiveInCodex = false, bool IsConnected = false, bool IsRefreshing = false,
    string? Error = null, bool IsActiveInClaudeCode = false)
{
    public bool IsActive => Profile.Provider == AccountProvider.ClaudeCode ? IsActiveInClaudeCode : IsActiveInCodex;
    public bool IsStale => Error is not null || (Snapshot is not null && (!IsActive || DateTimeOffset.UtcNow - Snapshot.FetchedAt > TimeSpan.FromMinutes(5)));
    /// <summary>Codex Pro only has a weekly quota: no 5-hour window is presented for it.</summary>
    public bool HasShortWindow => !(Profile.Provider == AccountProvider.Codex &&
        (Snapshot?.PlanType ?? Profile.ProviderPlanType)?.ToLowerInvariant() is "pro" or "prolite");
}
public sealed record TrackerState(IReadOnlyList<AccountState> Accounts, Guid? SelectedAccountId,
    bool IsBusy = false, string? StatusMessage = null, bool OnboardingComplete = false)
{
    public GlobalResetFeedState? GlobalResetFeed { get; init; }
    public ManualCodexReset? ManualCodexReset { get; init; }
    public IReadOnlyList<AccountState> ActiveAccounts => Accounts.Where(a => a.IsActive).OrderBy(a => a.Profile.Provider).ToArray();
    public AccountState? ActiveAccount => ActiveAccounts.FirstOrDefault();
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
    Task CheckGlobalResetAnnouncementsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    void SynchronizeGlobalResetMonitoring() { }
    Task SuspendAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(Guid id, CancellationToken cancellationToken = default);
    Task ImportCurrentAccountAsync(CancellationToken cancellationToken = default);
    Task CompleteOnboardingAsync(CancellationToken cancellationToken = default);
}
