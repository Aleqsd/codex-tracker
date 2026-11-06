using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Codex;
using CodexTracker.Core;

internal static class RefreshUiChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static void Publish(DemoTrackerService service, TrackerState state)
    {
        typeof(DemoTrackerService).GetProperty(nameof(service.State))!.SetValue(service, state);
        typeof(DemoTrackerService).GetMethod("Notify", Private)!.Invoke(service, null);
    }
    private static async Task Settle()
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(25); }

    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexTrackerRefreshUi", Guid.NewGuid().ToString("N"));
        var preferences = new PreferencesStore(false, root);
        await using var service = new DemoTrackerService();
        var window = new MainWindow(service, true, preferences, new UpdateService());
        try
        {
            await window.InitializeAsync();
            var clock = Field<DispatcherTimer>(window, "_clockTimer");
            Check(!clock.IsEnabled, "Background startup does not run the visual clock");
            window.ShowPanel(); await Settle(); window.UpdateLayout();
            Check(clock.IsEnabled, "Opening the dashboard resumes its visual clock");
            var model = (DashboardViewModel)window.DataContext;
            var updates = 0;
            model.PropertyChanged += (_, e) => { if (e.PropertyName is null) updates++; };
            var state = service.State;
            for (var i = 0; i < 12; i++) Publish(service, state with { StatusMessage = "fixture " + i });
            await Settle();
            Check(updates == 1, "A burst of collection events publishes one latest dashboard update");

            var forecastCalls = 0;
            var account = state.ActiveAccount!;
            var accountModel = new AccountViewModel(account, state, preferences, _ =>
            { forecastCalls++; return new(TimeSpan.FromHours(2), PreviewClock.UtcNow.AddHours(2), "fixture", UsageWindowKind.Weekly); });
            _ = accountModel.HasRecentWarning; _ = accountModel.RecentText; _ = accountModel.RecentAside; _ = accountModel.RecentHint;
            Check(accountModel.HasRecentWarning && forecastCalls == 1, "Recent-use bindings share one forecast for the presentation tick");
            accountModel.Tick(); _ = accountModel.RecentText; _ = accountModel.RecentHint;
            Check(forecastCalls == 2, "The following presentation tick obtains a fresh forecast once");

            window.ShowResets(); await Settle(); window.UpdateLayout();
            var resets = Field<ResetsView>(window, "_resets");
            var timeline = Field<StackPanel>(resets, "_timeline");
            var original = timeline.Children.Cast<UIElement>().ToArray();
            var bell = Field<List<(ResetScheduleEntry Entry, Button Button)>>(resets, "_alertButtons").First(pair => pair.Button.IsVisible && !pair.Entry.Account.IsActive).Button;
            var focused = Foreground.Focus(bell);
            resets.Update(state with { IsBusy = true, Accounts = state.Accounts.Select(a => a with { IsRefreshing = a.IsActive }).ToArray() });
            Check(original.SequenceEqual(timeline.Children.Cast<UIElement>()), "Busy-only refresh preserves the reset controls and their focus targets");
            if (focused) Check(Foreground.HasFocus(bell), "A progress-only refresh keeps keyboard focus on the reset bell");
            resets.Update(state);
            Check(original.SequenceEqual(timeline.Children.Cast<UIElement>()), "Unchanged completion does not rebuild the reset timeline");

            using var tray = new TrayController(window, service, preferences, () => Task.CompletedTask);
            var icon = Field<System.Windows.Forms.NotifyIcon>(tray, "_tray");
            var menu = icon.ContextMenuStrip!;
            var changed = account with { Snapshot = account.Snapshot! with { Buckets = [new("codex", "Codex", [new(61, 10080, DateTimeOffset.UtcNow.AddHours(4))])] } };
            var next = state with { Accounts = state.Accounts.Select(a => a.Profile.Id == account.Profile.Id ? changed : a).ToArray() };
            Publish(service, next); await Settle();
            if (focused)
            {
                var replacement = Field<List<(ResetScheduleEntry Entry, Button Button)>>(resets, "_alertButtons").FirstOrDefault(pair => Equals(pair.Button.Tag, bell.Tag)).Button;
                Check(replacement is not null && Foreground.HasFocus(replacement), "A new observation restores keyboard focus on the matching reset bell");
            }
            Check(ReferenceEquals(menu, icon.ContextMenuStrip), "Changing only quota values retains the tray menu structure");
            var area = TestScreen.Area;
            menu.Show(new System.Drawing.Point(area.Left + 40, area.Top + 40));
            Check(menu.Items.OfType<System.Windows.Forms.ToolStripMenuItem>().First(i => !i.Enabled).Text!.Contains("39%"),
                "Opening the existing tray menu reads the latest quota");
            menu.Close();

            window.OpenPage("Comptes"); await Settle();
            original = timeline.Children.Cast<UIElement>().ToArray();
            Publish(service, state); await Settle();
            Check(original.SequenceEqual(timeline.Children.Cast<UIElement>()), "An inactive reset tab defers rebuilding its controls");
            window.ShowResets(); await Settle();
            Check(!original.SequenceEqual(timeline.Children.Cast<UIElement>()), "Returning to the reset tab renders the latest observation");
            window.Hide();
            Check(!clock.IsEnabled, "Hiding the window stops the visual clock");
            Publish(service, next); await Settle();
            window.ShowPanel(); await Settle();
            Check(model.Active!.WeeklyNumber == "39%" && clock.IsEnabled, "Reopening a hidden window presents the latest collected state");
        }
        finally { window.PrepareExit(); window.Close(); }
        await CalendarLatestRequest(root);
    }

    private static async Task CalendarLatestRequest(string root)
    {
        var directory = Path.Combine(root, "calendar");
        new GoogleCalendarSecretStore(directory).Save(new(new("fixture.apps.googleusercontent.com", "fake-secret"), "fake-token", "demo@example.test"));
        using var handler = new PausedCalendar();
        using var calendar = new GoogleCalendarSync(directory, false, http: new HttpClient(handler));
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        TrackerState State(params string[] credits) => new([new(new(id, "demo@example.test"),
            new("demo@example.test", "pro", [], credits.Length, credits.Select(c => new ResetCredit(c, c, now.AddDays(-1), now.AddDays(2))).ToArray(), now), IsActiveInCodex: true)], id);
        var initial = calendar.SyncAsync(State("first", "obsolete"), true, p => p.Email);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var superseded = calendar.SyncAsync(State("intermediate"), true, p => p.Email);
        var latest = calendar.SyncAsync(State("latest"), true, p => p.Email);
        Check(!latest.IsCompleted, "A calendar refresh arriving during HTTP waits for the pending reconciliation");
        handler.Release.TrySetResult();
        await Task.WhenAll(initial, superseded, latest).WaitAsync(TimeSpan.FromSeconds(5));
        Check(handler.Inserted.SequenceEqual(new[] { GoogleCalendarSync.Key(id, "first", now.AddDays(2)), GoogleCalendarSync.Key(id, "latest", now.AddDays(2)) }),
            "Calendar reconciliation skips superseded additions and processes the latest request without a timer");
        Check(handler.MaximumConcurrent == 1, "Calendar reconciliation keeps simulated HTTP sends sequential");
    }

    private sealed class PausedCalendar : HttpMessageHandler
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Inserted = [];
        internal int MaximumConcurrent;
        private int _concurrent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            MaximumConcurrent = Math.Max(MaximumConcurrent, ++_concurrent);
            try
            {
                if (request.RequestUri!.Host == "oauth2.googleapis.com") return Json(new { access_token = "fake-access", expires_in = 3600 });
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    var id = body.RootElement.GetProperty("id").GetString()!;
                    Inserted.Add(id);
                    if (Inserted.Count == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
                    return Json(new { id, status = "confirmed" });
                }
                if (request.Method == HttpMethod.Get) return Json(new { id = request.RequestUri.Segments[^1], status = "confirmed" });
                throw new InvalidOperationException("Unexpected simulated Calendar request");
            }
            finally { _concurrent--; }
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
