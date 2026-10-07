namespace CodexTracker.App;

internal sealed class DemoTrackerService : ITrackerService
{
    public event EventHandler? Changed;
    public event EventHandler<QuotaNotification>? Notification { add { } remove { } }
    private readonly Dictionary<Guid, IReadOnlyList<UsageSample>> _history = new();
    public TrackerState State { get; private set; }
    public DemoTrackerService() : this(false) { }
    public DemoTrackerService(bool showAdvice, bool showExpectedResets = false, bool showGlobalResets = false, bool showClaudeCode = false, bool showManualReset = false)
    {
        var now = PreviewClock.UtcNow;
        string[] emails = ["alex@example.com", "studio@example.com", "projets@example.com", "recherche@example.com", "perso@example.com"];
        double[] weekly = [showAdvice ? 8 : 72, 18, 93, 8, 46];
        double[] fiveHour = [86, 64, 100, 32, 74];
        string[] plans = ["Pro", "Plus", "Pro", "Free", "Plus"];
        var accounts = emails.Select((email, i) => new AccountState(new AccountProfile(Guid.NewGuid(), email),
            new AccountSnapshot(email, plans[i], [new QuotaBucket("codex", "Codex", [new QuotaWindow(100 - fiveHour[i], 300, now.AddHours(2 + i).AddMinutes(14)), new QuotaWindow(100 - weekly[i], 10080, now.AddDays(2 + i % 3).AddHours(14).AddMinutes(32))])],
                i == 0 ? 3 : i % 3, [new ResetCredit($"demo-{i}", "Crédit de reset", now.AddDays(-5), i == 0 ? now.AddHours(23) : now.AddDays(4 + i))], i == 0 ? now.AddSeconds(-26) : showAdvice && i == 2 ? now.AddMinutes(-12) : now.AddHours(-2 * i).AddMinutes(-12),
                PlanMultiplier: i == 0 ? 20 : i == 2 ? 5 : null, SubscriptionStartedAt: i == 3 ? null : now.AddMonths(-4 - i), SubscriptionEndsAt: i == 3 ? null : now.AddDays(28 - i)), IsActiveInCodex: i == 0, IsConnected: true)).ToArray();
        if (showExpectedResets)
        {
            var snapshot = accounts[1].Snapshot!;
            accounts[1] = accounts[1] with { Snapshot = snapshot with {
                Buckets = [new("codex", "Codex", [new(82, 10080, now.AddMinutes(-12)), new(65, 300, now.AddMinutes(-12))])]
            } };
        }
        if (showManualReset) accounts = accounts.Select(a => a with { Snapshot = a.Snapshot! with { FetchedAt = now.AddHours(-1) } }).ToArray();
        if (showClaudeCode)
        {
            accounts = [.. accounts, new(new(Guid.NewGuid(), "alex@example.com", AccountProvider.ClaudeCode, "demo-personal", "Personnel"),
                new("alex@example.com", "max", [new("claude", "Claude Code", [new(34, 300, now.AddHours(3)), new(57, 10080, now.AddDays(4))])], null, null, now.AddMinutes(-1), PlanMultiplier: 20),
                IsActiveInClaudeCode: true, IsConnected: true),
                new(new(Guid.NewGuid(), "claude-pro@example.com", AccountProvider.ClaudeCode),
                    new("claude-pro@example.com", "pro", [new("claude", "Claude Code", [new(62, 300, now.AddHours(1)), new(22, 10080, now.AddDays(5))])], null, null, now.AddHours(-3)), IsConnected: true)];
            accounts = [.. accounts, new(new(Guid.NewGuid(), "alex@example.com", AccountProvider.ClaudeCode, "demo-company", "Entreprise Exemple"),
                new("alex@example.com", "team", [new("claude", "Claude Code", [new(2, 300, null), new(28, 10080, null)])], null, null, now.AddHours(-2)), IsConnected: true)];
        }
        State = new TrackerState(accounts, accounts[0].Profile.Id, OnboardingComplete: true);
        if (showGlobalResets) State = State with { GlobalResetFeed = new([
            new("demo-reset", "Source fictive", "https://example.com/reset-demo", "https://example.com/reset-scope-demo",
                now.AddHours(-2), now.AddHours(-1), now.AddMinutes(-1), ["plus", "pro"], [ResetKind.Weekly, ResetKind.Short])], now.AddMinutes(-1)) };
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
        var hours = (account?.Snapshot?.Weekly?.RemainingPercent ?? 72) / 1.5;
        return account?.IsActive == true
            ? new UsageForecast(TimeSpan.FromHours(hours), PreviewClock.UtcNow.AddHours(hours), "Au rythme récent, estimation indicative fondée sur les relevés de démonstration. Votre usage peut changer.", UsageWindowKind.Weekly)
            : new UsageForecast(null, null, "Ouvrez ce compte dans Codex pour obtenir une estimation fondée sur son utilisation récente.");
    }
    public Task InitializeAsync(CancellationToken cancellationToken = default) { Notify(); return Task.CompletedTask; }
    public Task SuspendAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken cancellationToken = default) => RefreshAsync(cancellationToken);
    public Task RefreshAsync(CancellationToken cancellationToken = default) { State = State with { Accounts = State.Accounts.Select(a => !a.IsActive ? a : a with { Snapshot = a.Snapshot is null ? null : a.Snapshot with { FetchedAt = PreviewClock.UtcNow } }).ToArray() }; Notify(); return Task.CompletedTask; }
    public Task RemoveAccountAsync(Guid id, CancellationToken cancellationToken = default) { State = State with { Accounts = State.Accounts.Where(a => a.Profile.Id != id).ToArray(), SelectedAccountId = State.SelectedAccountId == id ? State.Accounts.FirstOrDefault(a => a.Profile.Id != id)?.Profile.Id : State.SelectedAccountId }; Notify(); return Task.CompletedTask; }
    public Task ImportCurrentAccountAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CompleteOnboardingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
