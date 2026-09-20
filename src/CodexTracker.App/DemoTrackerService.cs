namespace CodexTracker.App;

internal sealed class DemoTrackerService : ITrackerService
{
    public event EventHandler? Changed;
    public event EventHandler<QuotaNotification>? Notification { add { } remove { } }
    private readonly Dictionary<Guid, IReadOnlyList<UsageSample>> _history = new();
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
        foreach (var account in accounts)
        {
            var samples = new List<UsageSample>();
            var end = account.Snapshot!.FetchedAt;
            for (int day = 6; day >= 0; day--)
            {
                int minutes = day == 0 ? 360 : 90;
                for (int minute = minutes; minute >= 0; minute -= 2)
                {
                    var stamp = end.AddDays(-day).AddMinutes(-minute);
                    double weeklyRemaining = Math.Clamp(account.Snapshot.Weekly!.RemainingPercent + day * 3.4 + minute * 0.025, 0, 100);
                    double shortRemaining = Math.Clamp(account.Snapshot.Short!.RemainingPercent + minute * 0.1, 0, 100);
                    samples.Add(new UsageSample(account.Profile.Id, stamp, weeklyRemaining, account.Snapshot.Weekly.ResetsAt, shortRemaining, stamp.Date.AddHours(24)));
                }
            }
            _history[account.Profile.Id] = samples;
        }
    }
    public IReadOnlyList<UsageSample> GetHistory(Guid accountId) => _history.GetValueOrDefault(accountId) ?? [];
    public UsageForecast GetForecast(Guid accountId)
    {
        var account = State.Accounts.FirstOrDefault(a => a.Profile.Id == accountId);
        return account?.IsActiveInCodex == true
            ? new UsageForecast(TimeSpan.FromHours(48), DateTimeOffset.UtcNow.AddHours(48), "Au rythme récent, estimation indicative fondée sur les relevés de démonstration. Votre usage peut changer.", UsageWindowKind.Weekly)
            : new UsageForecast(null, null, "Ouvrez ce compte dans Codex pour obtenir une estimation fondée sur son utilisation récente.");
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
