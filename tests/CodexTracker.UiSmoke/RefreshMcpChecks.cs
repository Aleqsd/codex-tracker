using System.IO;
using System.Diagnostics;
using System.Text;
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
        service.Statuses = service.Statuses.Select(s => s with { ErrorCode = "persistence_failed" }).ToArray();
        Check(TrackerControl.RefreshReport(service, "fixture").Providers.All(p => p.ErrorCode == "persistence_failed"),
            "Refresh preserves the safe persistence failure code without exposing exception text");
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
            await RevocationAcrossProcesses(window, control, preferences, service);
        }
        finally { window.PrepareExit(); window.Close(); }
    }

    private static async Task RevocationAcrossProcesses(MainWindow window, TrackerControl control, PreferencesStore preferences, FakeService service)
    {
        var instance = Guid.NewGuid().ToString("D");
        var arguments = new[] { "--demo", "--demo-instance", instance };
        // A synthetic in-process tracker hosts the real pipe protocol. Child MCP/CLI processes
        // receive only this unique demo instance and never start a real collector or send messages.
        await using var server = new LocalServer(window, control, arguments);
        await using var mcp = new McpProcess(arguments);
        await mcp.Initialize();
        var baseline = service.RefreshCalls;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Pending = release.Task;
        var mcpReply = mcp.Refresh();
        var cliReply = RunCli(arguments);
        try
        {
            using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (service.RefreshCalls < baseline + 2) await Task.Delay(10, waiting.Token);
            preferences.Update(p => p with { McpEnabled = false });
            Check(!service.Pending.IsCanceled && !service.Pending.IsCompleted,
                "Disabling Assistants does not cancel the shared collection");
        }
        finally { release.TrySetResult(); }
        var deniedMcp = await mcpReply.WaitAsync(TimeSpan.FromSeconds(15));
        var deniedCli = await cliReply.WaitAsync(TimeSpan.FromSeconds(15));
        Check(Denied(deniedMcp) && deniedCli.Code == 1 && Denied(deniedCli.Reply),
            "In-flight MCP and CLI replies are refused after access is disabled, with no provider result");
        baseline = service.RefreshCalls;
        var stillDeniedMcp = await mcp.Refresh();
        var stillDeniedCli = await RunCli(arguments);
        Check(Denied(stillDeniedMcp) && stillDeniedCli.Code == 1 && Denied(stillDeniedCli.Reply) && service.RefreshCalls == baseline,
            "Repeated MCP and CLI requests stay refused without starting collection while disabled");
        preferences.Update(p => p with { McpEnabled = true });
        service.Pending = Task.CompletedTask;
        var revision = preferences.Revision;
        for (var repetition = 0; repetition < 2; repetition++)
        {
            var enabledMcp = mcp.Refresh();
            var enabledCli = RunCli(arguments);
            var approvedMcp = await enabledMcp;
            var approvedCli = await enabledCli;
            Check(approvedMcp.GetProperty("result").GetProperty("refreshed").GetBoolean() && approvedCli.Code == 0 &&
                approvedCli.Reply.GetProperty("result").GetProperty("refreshed").GetBoolean(),
                "Re-enabled MCP and CLI requests succeed concurrently and can be repeated");
        }
        Check(preferences.Revision == revision, "Repeated process refreshes preserve the preference revision");
        static bool Denied(JsonElement reply) => reply.TryGetProperty("error", out var error) && error.GetString() == "mcp_disabled" &&
            !reply.TryGetProperty("result", out _);
    }

    private static Process StartBridge(string[] demoArguments, params string[] mode)
    {
        var executable = Environment.GetEnvironmentVariable("CODEX_TRACKER_TEST_EXECUTABLE") ??
            Path.ChangeExtension(typeof(MainWindow).Assembly.Location, ".exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in demoArguments.Concat(mode)) start.ArgumentList.Add(argument);
        // A developer SDK may be installed per-user rather than registered for apphost discovery.
        start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return Process.Start(start) ?? throw new IOException("Cannot start the fictional MCP/CLI test client.");
    }

    private static async Task<(int Code, JsonElement Reply)> RunCli(string[] arguments)
    {
        using var process = StartBridge(arguments, "--cli", "refresh_active_account");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            using var document = JsonDocument.Parse(await output);
            _ = await errors;
            return (process.ExitCode, document.RootElement.Clone());
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private sealed class McpProcess(string[] arguments) : IAsyncDisposable
    {
        private readonly Process _process = StartBridge(arguments, "--mcp");
        private Task<string>? _errors;
        private int _sequence;
        public async Task Initialize()
        {
            _errors = _process.StandardError.ReadToEndAsync();
            await Rpc("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "refresh-revocation-fixture", version = "1.0" } });
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            await _process.StandardInput.FlushAsync();
        }
        public async Task<JsonElement> Refresh()
        {
            var result = await Rpc("tools/call", new { name = "refresh_active_account", arguments = new { } });
            var text = result.GetProperty("content").EnumerateArray().First(c => c.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        private async Task<JsonElement> Rpc(string method, object parameters)
        {
            var id = ++_sequence;
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            await _process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var reply = document.RootElement;
                if (!reply.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                if (reply.TryGetProperty("error", out _)) throw new InvalidOperationException("MCP fixture protocol failed.");
                return reply.GetProperty("result").Clone();
            }
            throw new IOException("MCP fixture process closed before replying.");
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (_errors is not null) _ = await _errors;
            _process.Dispose();
        }
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
