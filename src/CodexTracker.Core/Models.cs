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
    int? AvailableResetCredits, IReadOnlyList<ResetCredit>? ResetCredits, DateTimeOffset FetchedAt)
{
    public QuotaWindow? Weekly => Buckets.FirstOrDefault(b => b.Id == "codex")?.Windows
        .Where(w => w.IsWeekly).OrderBy(w => w.RemainingPercent).FirstOrDefault();
}
public sealed record AccountState(AccountProfile Profile, AccountSnapshot? Snapshot = null,
    bool IsActiveInCodex = false, bool IsConnected = false, bool IsRefreshing = false,
    string? Error = null)
{
    public bool IsStale => Error is not null || (Snapshot is not null && DateTimeOffset.UtcNow - Snapshot.FetchedAt > TimeSpan.FromMinutes(5));
}
public sealed record TrackerState(IReadOnlyList<AccountState> Accounts, Guid? SelectedAccountId,
    bool IsBusy = false, string? StatusMessage = null, bool CanSwitch = false,
    string? SwitchUnavailableReason = null, bool OnboardingComplete = false,
    string? PendingSwitchEmail = null)
{
    public AccountState? SelectedAccount => Accounts.FirstOrDefault(a => a.Profile.Id == SelectedAccountId);
}
public sealed record LoginPrompt(Uri Url, string? UserCode = null);
public sealed record SwitchResult(bool Success, string Message, bool NeedsUserVerification = false);

public interface ITrackerService : IAsyncDisposable
{
    event EventHandler? Changed;
    TrackerState State { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task AddAccountAsync(string email, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(Guid id, CancellationToken cancellationToken = default);
    Task SelectAccountAsync(Guid id, CancellationToken cancellationToken = default);
    Task ConnectAccountAsync(Guid id, Action<LoginPrompt> onLogin, CancellationToken cancellationToken = default);
    Task ImportCurrentAccountAsync(CancellationToken cancellationToken = default);
    Task<SwitchResult> SwitchAccountAsync(Guid id, bool confirmed, CancellationToken cancellationToken = default);
    Task<SwitchResult> ConfirmSwitchAsync(bool accepted, CancellationToken cancellationToken = default);
    Task CompleteOnboardingAsync(CancellationToken cancellationToken = default);
}
