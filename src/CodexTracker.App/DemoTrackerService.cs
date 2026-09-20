namespace CodexTracker.App;

internal sealed class DemoTrackerService : ITrackerService
{
    public event EventHandler? Changed;
    public TrackerState State { get; private set; }
    public DemoTrackerService()
    {
        var now = DateTimeOffset.UtcNow;
        string[] emails = ["alex@example.com", "studio@example.com", "projets@example.com", "recherche@example.com", "perso@example.com"];
        double[] weekly = [72, 18, 93, 8, 46];
        double[] fiveHour = [86, 64, 100, 32, 74];
        string[] plans = ["Pro", "Plus", "Pro", "Free", "Plus"];
        var accounts = emails.Select((email, i) => new AccountState(new AccountProfile(Guid.NewGuid(), email),
            new AccountSnapshot(email, plans[i], [new QuotaBucket("codex", "Codex", [new QuotaWindow(100 - fiveHour[i], 300, now.AddHours(2 + i).AddMinutes(14)), new QuotaWindow(100 - weekly[i], 10080, now.AddDays(2 + i % 3).AddHours(14).AddMinutes(32))])],
                i == 0 ? 3 : i % 3, [new ResetCredit($"demo-{i}", "Crédit de reset", now.AddDays(-5), now.AddDays(30 + i))], i == 0 ? now.AddSeconds(-26) : now.AddHours(-2 * i).AddMinutes(-12),
                PlanMultiplier: i == 0 ? 20 : i == 2 ? 5 : null, SubscriptionStartedAt: i == 3 ? null : now.AddMonths(-4 - i), SubscriptionEndsAt: i == 3 ? null : now.AddDays(28 - i)), IsActiveInCodex: i == 0, IsConnected: true)).ToArray();
        State = new TrackerState(accounts, accounts[0].Profile.Id, OnboardingComplete: true);
    }
    public Task InitializeAsync(CancellationToken cancellationToken = default) { Notify(); return Task.CompletedTask; }
    public Task RefreshAsync(CancellationToken cancellationToken = default) { State = State with { Accounts = State.Accounts.Select(a => !a.IsActiveInCodex ? a : a with { Snapshot = a.Snapshot is null ? null : a.Snapshot with { FetchedAt = DateTimeOffset.UtcNow } }).ToArray() }; Notify(); return Task.CompletedTask; }
    public Task RemoveAccountAsync(Guid id, CancellationToken cancellationToken = default) { State = State with { Accounts = State.Accounts.Where(a => a.Profile.Id != id).ToArray(), SelectedAccountId = State.SelectedAccountId == id ? State.Accounts.FirstOrDefault(a => a.Profile.Id != id)?.Profile.Id : State.SelectedAccountId }; Notify(); return Task.CompletedTask; }
    public Task SelectAccountAsync(Guid id, CancellationToken cancellationToken = default) { State = State with { SelectedAccountId = id }; Notify(); return Task.CompletedTask; }
    public Task ImportCurrentAccountAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CompleteOnboardingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
