using System.IO;
using System.Text.Json;
using CodexTracker.App;
using CodexTracker.App.Mcp;
using CodexTracker.App.Updates;
using CodexTracker.Core;

internal static class RefreshMcpChecks
{
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, TrackerControl.Json);

    internal static async Task Run()
    {
        await using var demo = new DemoTrackerService(false, showClaudeCode: true);
        var accounts = demo.State.ActiveAccounts;
        var now = DateTimeOffset.UtcNow;
        ProviderRefreshStatus Success(AccountState account) => new(account.Profile.Provider, account.Profile.Id,
            RefreshOutcome.Unchanged, now.AddSeconds(-1), now, account.Snapshot!.FetchedAt, 1000, now.AddMinutes(2), RefreshReason.Manual);
        await using var service = new FakeService { State = demo.State, Statuses = accounts.Select(Success).ToArray() };

        var result = TrackerControl.RefreshReport(service, "fixture");
        Check(result.Refreshed && result.Status == "success" && result.Providers.Count == 2, "Refresh reports success independently for both active providers");
        Check(result.Providers.All(p => p.Outcome == "unchanged" && p.Reason == "manual" && p.DurationMilliseconds == 1000),
            "Refresh exposes string outcomes, reasons and attempt timing");
        var completed = service.Statuses;
        service.Statuses = completed.Select(s => s with { Outcome = RefreshOutcome.Refreshing, CompletedAt = null }).ToArray();
        result = TrackerControl.RefreshReport(service, "fixture", completed);
        Check(result.Refreshed && result.Providers.All(p => p.Outcome == "unchanged" && p.CompletedAt == now),
            "Refresh preserves the completed requested pass when a background pass has already started");
        Check(TrackerControl.RefreshReport(service, "fixture").Providers.All(p => p.Outcome == "refreshing"),
            "Status diagnostics still describe the currently running background pass");
        var oldObservation = accounts[0].Snapshot!.FetchedAt;
        service.Statuses = [Success(accounts[0]), Success(accounts[1]) with { Outcome = RefreshOutcome.Failed, ErrorCode = "fixture-private-exception" }];
        result = TrackerControl.RefreshReport(service, "fixture");
        Check(!result.Refreshed && result.Status == "partial" && result.ObservedAt == oldObservation,
            "A failed active provider produces partial status despite preserved historical quotas");
        Check(result.Providers[1].ErrorCode == "collection_failed" && !JsonSerializer.Serialize(result).Contains("fixture-private-exception"),
            "Refresh never exposes arbitrary collector error text");
        service.Statuses = service.Statuses.Select(s => s with { Outcome = RefreshOutcome.Failed, ErrorCode = "RequestTimedOut" }).ToArray();
        result = TrackerControl.RefreshReport(service, "fixture");
        Check(!result.Refreshed && result.Status == "failed" && result.Providers.All(p => p.ErrorCode == "requestTimedOut"),
            "Failed refresh retains observations without claiming success and normalizes known errors");
        service.Statuses = service.Statuses.Select(s => s with { Outcome = RefreshOutcome.Suspended }).ToArray();
        result = TrackerControl.RefreshReport(service, "fixture");
        Check(!result.Refreshed && result.Status == "suspended", "Suspended collection is explicit");
        service.State = new([], null);
        service.Statuses = [new(AccountProvider.Codex, null, RefreshOutcome.Unavailable), new(AccountProvider.ClaudeCode, null, RefreshOutcome.Unavailable)];
        result = TrackerControl.RefreshReport(service, "fixture");
        Check(!result.Refreshed && result.Status == "unavailable" && result.ObservedAt is null, "No active source does not count as a successful refresh");
        result = TrackerControl.RefreshReport(demo, "fixture");
        Check(!result.Refreshed && result.Status == "unavailable" && result.Providers.All(p => p.AttemptedAt is null),
            "Demo snapshots do not invent a completed collection");

        service.State = demo.State;
        service.Statuses = accounts.Select(Success).ToArray();
        var root = Path.Combine(Path.GetTempPath(), "CodexTrackerRefreshMcp", Guid.NewGuid().ToString("N"));
        var preferences = new PreferencesStore(false, root);
        var window = new MainWindow(service, true, preferences, new UpdateService());
        using var control = new TrackerControl(window, true);
        try
        {
            await window.InitializeAsync();
            var refused = false;
            try { await control.HandleAsync("refresh", Element(new { })); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("mcp_disabled")) { refused = true; }
            Check(refused && service.RefreshCalls == 0, "Disabled Assistants refuse refresh before starting collection");
            preferences.Update(p => p with { McpEnabled = true });
            refused = false;
            try { await control.HandleAsync("refresh", Element(new { unexpected = true })); }
            catch (ArgumentException) { refused = true; }
            Check(refused && service.RefreshCalls == 0, "Refresh refuses unexpected arguments before starting collection");
            var revision = preferences.Revision;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Pending = release.Task;
            var calls = Enumerable.Range(0, 4).Select(_ => control.HandleAsync("refresh", Element(new { }))).ToArray();
            Check(calls.All(call => !call.IsCompleted), "Concurrent MCP refresh requests await their collection");
            release.TrySetResult();
            var replies = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
            Check(replies.Cast<RefreshReply>().All(r => r.Refreshed && r.Status == "success" && r.Providers.Count == 2),
                "Concurrent MCP requests receive complete provider results");
            var repeated = (RefreshReply)await control.HandleAsync("refresh", Element(new { }));
            Check(repeated.Refreshed && preferences.Revision == revision, "Repeated refresh does not mutate preference revision");
            var status = Element(await control.HandleAsync("status", Element(new { })));
            Check(status.GetProperty("refresh").GetProperty("providers").GetArrayLength() == 2,
                "get_status exposes the same collection diagnostics without refreshing");
        }
        finally { window.PrepareExit(); window.Close(); }
    }

    private sealed class FakeService : ITrackerService
    {
        public TrackerState State { get; set; } = new([], null);
        public IReadOnlyList<ProviderRefreshStatus> Statuses { get; set; } = [];
        public Task Pending { get; set; } = Task.CompletedTask;
        public int RefreshCalls { get; private set; }
        public event EventHandler? Changed { add { } remove { } }
        public event EventHandler<QuotaNotification>? Notification { add { } remove { } }
        public IReadOnlyList<ProviderRefreshStatus> GetRefreshStatus() => Statuses;
        public IReadOnlyList<UsageSample> GetHistory(Guid id) => [];
        public UsageForecast GetForecast(Guid id) => new(null, null, "fixture");
        public Task InitializeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task RefreshAsync(CancellationToken token = default) { RefreshCalls++; return Pending; }
        public Task SuspendAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task RemoveAccountAsync(Guid id, CancellationToken token = default) => Task.CompletedTask;
        public Task ImportCurrentAccountAsync(CancellationToken token = default) => RefreshAsync(token);
        public Task CompleteOnboardingAsync(CancellationToken token = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
