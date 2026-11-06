using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Codex;
using CodexTracker.Core;

internal static class RefreshCalendarChecks
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
    private static async Task Settle()
    { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(25); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void VerifyAgain(GoogleCalendarSync calendar) =>
        typeof(GoogleCalendarSync).GetField("_nextVerification", Private)!.SetValue(calendar, DateTimeOffset.MinValue);
    private static Task CurrentSync(GoogleCalendarSync calendar) =>
        (Task)typeof(GoogleCalendarSync).GetField("_syncTask", Private)!.GetValue(calendar)!;
    private static void Publish(DemoTrackerService service, TrackerState state) =>
        typeof(DemoTrackerService).GetProperty(nameof(service.State))!.SetValue(service, state);
    private static void Credentials(string directory) => new GoogleCalendarSecretStore(directory)
        .Save(new(new("fixture.apps.googleusercontent.com", "fake-secret"), "fake-token", "demo@example.test"));
    private static TrackerState State(Guid id, DateTimeOffset now, int count) => new([new(new(id, "demo@example.test"),
        new("demo@example.test", "pro", [], count,
            Enumerable.Range(1, count).Select(i => new ResetCredit("credit-" + i, "Reserve " + i, now.AddDays(-1), now.AddDays(2).AddMinutes(i))).ToArray(), now),
        IsActiveInCodex: true)], id);
    private static string[] Keys(TrackerState state) => state.Accounts[0].Snapshot!.ResetCredits!
        .Select(c => GoogleCalendarSync.Key(state.Accounts[0].Profile.Id, c.Id, c.ExpiresAt!.Value)).ToArray();
    private static void SeedLinks(string directory, TrackerState state)
    {
        var store = new CalendarLinkStore(directory);
        foreach (var credit in state.Accounts[0].Snapshot!.ResetCredits!)
            store.Put(new(GoogleCalendarSync.Key(state.Accounts[0].Profile.Id, credit.Id, credit.ExpiresAt!.Value),
                CalendarLinkSource.Google, CalendarLinkStatus.Confirmed, credit.ExpiresAt.Value, DateTimeOffset.UtcNow));
    }
    private static PreferencesStore Preferences(string directory, bool autoAdd)
    {
        var preferences = new PreferencesStore(false, directory);
        preferences.Update(p => p with { GoogleCalendarAutoAdd = autoAdd, DownloadUpdatesAutomatically = false, ReserveMailAlerts = false });
        return preferences;
    }
    private static UpdateService Updates(string directory) => new("1.0.4", new HttpClient(new RejectNetwork()), Path.Combine(directory, "updates"));

    internal static async Task Run(string root)
    {
        await AutoAddPreferenceStopsPending(root);
        await VerificationUpdatesVisibleViews(root);
        await VerificationVisitsEveryEvent(root);
        await PartialVerificationStillNotifies(root);
    }

    private static async Task AutoAddPreferenceStopsPending(string root)
    {
        var directory = Path.Combine(root, "calendar-preference"); Credentials(directory);
        using var handler = new CalendarHttp { PauseFirstInsert = true };
        var calendar = new GoogleCalendarSync(directory, false, http: new HttpClient(handler));
        var preferences = Preferences(directory, true);
        await using var service = new DemoTrackerService();
        var state = State(Guid.NewGuid(), DateTimeOffset.UtcNow, 3) with { IsBusy = true };
        Publish(service, state);
        // Initialization would start unrelated update timers. The real preference and calendar wiring is already live.
        var window = new MainWindow(service, false, preferences, Updates(directory), calendar);
        try
        {
            var initial = window.SyncCalendarAsync();
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            window.Commands.SavePreferences(p => p with { GoogleCalendarAutoAdd = false });
            handler.Release.TrySetResult();
            await initial.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!preferences.Current.GoogleCalendarAutoAdd && handler.Inserted.SequenceEqual(Keys(state).Take(1)),
                "Disabling automatic Calendar additions stops the next insertion in an in-flight batch, even during collection");
            Check(calendar.Link(Keys(state)[1]) is null && calendar.Link(Keys(state)[2]) is null,
                "Disabling automatic Calendar additions leaves the unsent reserves untouched");
            window.Commands.SavePreferences(p => p with { GoogleCalendarAutoAdd = true });
            await CurrentSync(calendar).WaitAsync(TimeSpan.FromSeconds(5));
            Check(handler.Inserted.SequenceEqual(Keys(state)), "Re-enabling automatic Calendar additions immediately reconciles the pending reserves");
            Check(handler.MaximumConcurrent == 1, "Calendar preference changes keep simulated requests serial");
        }
        finally { handler.Release.TrySetResult(); window.PrepareExit(); window.Close(); }
    }

    private static async Task VerificationUpdatesVisibleViews(string root)
    {
        var directory = Path.Combine(root, "calendar-ui-verification"); Credentials(directory);
        var state = State(Guid.NewGuid(), DateTimeOffset.UtcNow, 4); var keys = Keys(state);
        SeedLinks(directory, state);
        using var handler = new CalendarHttp();
        var calendar = new GoogleCalendarSync(directory, false, http: new HttpClient(handler));
        await using var service = new DemoTrackerService(); Publish(service, state);
        var window = new MainWindow(service, false, Preferences(directory, false), Updates(directory), calendar);
        var notifications = 0; calendar.Changed += (_, _) => notifications++;
        try
        {
            window.ShowResets(); await Settle(); window.UpdateLayout();
            Check(Descendants(window).OfType<TextBlock>().Any(t => Equals(t.Tag, "CalendarStatus") && t.Text == Loc.T("Dans Google Agenda")),
                "The reset view initially presents confirmed Calendar events");
            handler.Missing.Add(keys[0]); handler.Cancelled.Add(keys[1]);
            await window.SyncCalendarAsync(); await Settle(); window.UpdateLayout();
            Check(calendar.Link(keys[0])?.Status == CalendarLinkStatus.Deleted && calendar.Link(keys[1])?.Status == CalendarLinkStatus.Deleted && notifications == 1,
                "One verification batch publishes one change for both missing and cancelled Calendar events");
            Check(Descendants(window).OfType<TextBlock>().Count(t => Equals(t.Tag, "CalendarStatus") && t.Text == Loc.T("Supprimé de Google Agenda")) >= 2,
                "Remote Calendar deletions update the visible reset statuses without changing tabs or collecting quotas");

            window.OpenPage("Calendrier"); await Settle(); window.UpdateLayout();
            Check(Descendants(window).OfType<TextBlock>().Any(t => t.IsVisible && t.Text == Loc.F("{0} sur {1} resets en réserve dans l’agenda", 2, 4)),
                "Calendar settings show the revised event coverage");
            handler.Missing.Add(keys[2]); VerifyAgain(calendar);
            await window.SyncCalendarAsync(); await Settle(); window.UpdateLayout();
            Check(notifications == 2 && Descendants(window).OfType<TextBlock>().Any(t => t.IsVisible && t.Text == Loc.F("{0} sur {1} resets en réserve dans l’agenda", 1, 4)),
                "A further remote deletion updates the open Calendar settings without reloading the page");
            Check(handler.Inserted.Count == 0 && handler.MaximumConcurrent == 1, "Calendar verification does not recreate deleted events or overlap simulated requests");
        }
        finally { window.PrepareExit(); window.Close(); }
    }

    private static async Task VerificationVisitsEveryEvent(string root)
    {
        var directory = Path.Combine(root, "calendar-verification-fairness"); Credentials(directory);
        var state = State(Guid.NewGuid(), DateTimeOffset.UtcNow, 51); var keys = Keys(state);
        SeedLinks(directory, state);
        using var handler = new CalendarHttp();
        using var calendar = new GoogleCalendarSync(directory, false, http: new HttpClient(handler));
        var notifications = 0; calendar.Changed += (_, _) => notifications++;
        for (var cycle = 0; cycle < 3; cycle++)
        {
            VerifyAgain(calendar);
            var previous = handler.Read.Count;
            await calendar.SyncAsync(state, false, p => p.Email);
            Check(handler.Read.Count - previous == 25, "Calendar verification keeps its 25-event request limit in cycle " + (cycle + 1));
        }
        Check(handler.Read.Distinct().Count() == 51 && keys.All(k => calendar.Link(k)?.VerifiedAt is not null),
            "Successive bounded verification cycles visit all 51 events instead of repeating the earliest expirations");
        Check(notifications == 3, "Successful verification emits one presentation update per batch");
    }

    private static async Task PartialVerificationStillNotifies(string root)
    {
        var directory = Path.Combine(root, "calendar-partial-verification"); Credentials(directory);
        var state = State(Guid.NewGuid(), DateTimeOffset.UtcNow, 3); var keys = Keys(state);
        SeedLinks(directory, state);
        using var handler = new CalendarHttp { Failure = keys[1] }; handler.Missing.Add(keys[0]);
        using var calendar = new GoogleCalendarSync(directory, false, http: new HttpClient(handler));
        var notifications = 0; calendar.Changed += (_, _) => notifications++;
        await calendar.SyncAsync(state, false, p => p.Email);
        Check(calendar.Link(keys[0])?.Status == CalendarLinkStatus.Deleted && calendar.Error is not null && notifications == 1,
            "A partially failed verification publishes the persisted deletion and error together once");
        Check(handler.Read.Count == 2 && calendar.Link(keys[2])?.VerifiedAt is null,
            "A failed verification leaves unvisited Calendar events unconfirmed for the next attempt");
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Update HTTP is forbidden in Calendar refresh checks");
    }

    private sealed class CalendarHttp : HttpMessageHandler
    {
        internal bool PauseFirstInsert;
        internal string? Failure;
        internal readonly HashSet<string> Missing = [], Cancelled = [];
        internal readonly List<string> Inserted = [], Read = [];
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    var id = body.RootElement.GetProperty("id").GetString()!; Inserted.Add(id);
                    if (PauseFirstInsert && Inserted.Count == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
                    return Json(new { id, status = "confirmed" });
                }
                if (request.Method == HttpMethod.Get)
                {
                    var id = request.RequestUri.Segments[^1]; Read.Add(id);
                    if (Missing.Contains(id)) return new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
                    if (Failure == id) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
                    return Json(new { id, status = Cancelled.Contains(id) ? "cancelled" : "confirmed" });
                }
                throw new InvalidOperationException("Unexpected simulated Calendar request");
            }
            finally { _concurrent--; }
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
