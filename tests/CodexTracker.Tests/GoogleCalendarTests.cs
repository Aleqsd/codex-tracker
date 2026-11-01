using System.Net;
using System.Text;
using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class GoogleCalendarTests
{
    private static readonly GoogleOAuthClient Client = new("123-abc.apps.googleusercontent.com", "GOCSPX-fake_secret");
    private static readonly GoogleCalendarSecrets Connected = new(Client, "fake-refresh-token", "demo@example.test");
    private static readonly CalendarEntry Entry = new(CalendarExport.Identity(Guid.Parse("33333333-3333-3333-3333-333333333333"), "credit", "c1",
        DateTimeOffset.Parse("2026-11-30T20:35:40Z")) + "@codex-tracker.local", "Compte · Expiration d’un reset en réserve", "Description", DateTimeOffset.Parse("2026-11-30T20:35:40Z"));

    [Fact]
    public void OnlyADesktopClientFileIsAccepted()
    {
        var parsed = GoogleOAuthClient.Parse("{\"installed\":{\"client_id\":\"123-abc.apps.googleusercontent.com\",\"project_id\":\"codex-tracker-123\",\"client_secret\":\"GOCSPX-fake_secret\",\"redirect_uris\":[\"http://localhost\"]}}");
        Assert.True(Client.SameAs(parsed));
        Assert.Equal("codex-tracker-123", parsed.ProjectId);
        Assert.Null(GoogleOAuthClient.Parse("{\"installed\":{\"client_id\":\"123-abc.apps.googleusercontent.com\",\"project_id\":\"../evil\",\"client_secret\":\"x\"}}").ProjectId);
        Assert.Throws<ArgumentException>(() => GoogleOAuthClient.Parse("{\"web\":{\"client_id\":\"123-abc.apps.googleusercontent.com\",\"client_secret\":\"x\"}}"));
        Assert.Throws<ArgumentException>(() => GoogleOAuthClient.Parse("{\"installed\":{\"client_id\":\"evil.example.test\",\"client_secret\":\"x\"}}"));
        Assert.Throws<ArgumentException>(() => GoogleOAuthClient.Parse("not json"));
    }

    [Fact]
    public void AuthorizationUsesPkceLoopbackAndTheEventsScopeOnly()
    {
        var verifier = GoogleCalendarClient.RandomToken();
        var uri = GoogleCalendarClient.AuthorizationUri(Client, new("http://127.0.0.1:50123/"), GoogleCalendarClient.Challenge(verifier), "state1").AbsoluteUri;
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", uri);
        Assert.Contains("code_challenge_method=S256", uri);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A50123%2F", uri);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/calendar.events"), uri);
        Assert.DoesNotContain(Uri.EscapeDataString("https://www.googleapis.com/auth/calendar "), uri);
        Assert.DoesNotContain(verifier, uri);
    }

    [Fact]
    public void TheLoopbackAcceptsOnlyTheExpectedStateOnTheRootPath()
    {
        Assert.Equal("abc", GoogleLoopbackReceiver.Parse("GET /?state=s1&code=abc&scope=x HTTP/1.1", "s1").Code);
        Assert.Null(GoogleLoopbackReceiver.Parse("GET /?state=other&code=abc HTTP/1.1", "s1").Code);
        Assert.Equal(404, GoogleLoopbackReceiver.Parse("GET /favicon.ico HTTP/1.1", "s1").Status);
        Assert.Equal(404, GoogleLoopbackReceiver.Parse("POST /?state=s1&code=abc HTTP/1.1", "s1").Status);
        Assert.Equal(404, GoogleLoopbackReceiver.Parse(null, "s1").Status);
        Assert.NotNull(GoogleLoopbackReceiver.Parse("GET /?state=s1&error=access_denied HTTP/1.1", "s1").Error);
    }

    [Fact]
    public async Task TheLoopbackReceivesTheCodeFromABrowserRedirect()
    {
        using var receiver = new GoogleLoopbackReceiver();
        Assert.Equal("127.0.0.1", receiver.RedirectUri.Host);
        var code = receiver.ReceiveCodeAsync("s1", new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(new Uri(receiver.RedirectUri, "favicon.ico"))).StatusCode);
        var page = await http.GetStringAsync(new Uri(receiver.RedirectUri, "?state=s1&code=the-code"));
        Assert.Contains("Codex Tracker", page);
        Assert.Equal("the-code", await code);
    }

    [Fact]
    public async Task InsertCreatesTheEventOnceWithStableIdAndReminders()
    {
        var google = new FakeGoogle();
        var client = new GoogleCalendarClient(new HttpClient(google));
        var created = await client.InsertAsync(Connected, Entry, CancellationToken.None);
        Assert.Equal(CalendarExport.Key(Entry), created.Id);
        Assert.False(created.IsCancelled);
        Assert.StartsWith("https://www.google.com/calendar/", created.HtmlLink);
        using var body = JsonDocument.Parse(google.Bodies.Single(b => b.Contains("\"summary\"")));
        Assert.Equal(CalendarExport.Key(Entry), body.RootElement.GetProperty("id").GetString());
        Assert.Equal("2026-11-30T20:35:40Z", body.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal([4320, 1440, 60], body.RootElement.GetProperty("reminders").GetProperty("overrides").EnumerateArray().Select(o => o.GetProperty("minutes").GetInt32()));
        // Repeated request: Google answers 409, the existing event is returned, nothing duplicated.
        var again = await client.InsertAsync(Connected, Entry, CancellationToken.None);
        Assert.Equal(created.Id, again.Id);
        Assert.Single(google.Events);
        Assert.Equal(1, google.TokenRequests);
        Assert.All(google.Bodies, b => Assert.DoesNotContain("fake-refresh-token", b.Contains("grant_type") ? "" : b));
    }

    [Fact]
    public async Task ADeletedEventIsDetectedAndOnlyRestoredOnRequest()
    {
        var google = new FakeGoogle();
        var client = new GoogleCalendarClient(new HttpClient(google));
        await client.InsertAsync(Connected, Entry, CancellationToken.None);
        google.Events[CalendarExport.Key(Entry)] = "cancelled";
        Assert.True((await client.GetAsync(Connected, CalendarExport.Key(Entry), CancellationToken.None))!.IsCancelled);
        Assert.Null(await client.GetAsync(Connected, new string('a', 64), CancellationToken.None));
        var restored = await client.InsertAsync(Connected, Entry, CancellationToken.None);
        Assert.False(restored.IsCancelled);
        Assert.Contains(google.Methods, m => m == "PUT");
    }

    [Fact]
    public async Task RevokedAccessAsksToReconnectWithoutLeakingTheToken()
    {
        var google = new FakeGoogle { TokenError = "invalid_grant" };
        var client = new GoogleCalendarClient(new HttpClient(google));
        var error = await Assert.ThrowsAsync<GoogleCalendarException>(() => client.InsertAsync(Connected, Entry, CancellationToken.None));
        Assert.True(error.RequiresReconnect);
        Assert.DoesNotContain("fake-refresh-token", error.Message);
        Assert.Empty(google.Events);
        await Assert.ThrowsAsync<GoogleCalendarException>(() => client.InsertAsync(new(Client), Entry, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"accessNotConfigured\"}]}}", GoogleCalendarProblem.ApiDisabled, false)]
    [InlineData("{\"error\":{\"code\":403,\"status\":\"PERMISSION_DENIED\",\"details\":[{\"reason\":\"SERVICE_DISABLED\"}]}}", GoogleCalendarProblem.ApiDisabled, false)]
    [InlineData("{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"insufficientPermissions\"}]}}", GoogleCalendarProblem.MissingScope, true)]
    [InlineData("{\"error\":{\"code\":403,\"errors\":[{\"reason\":\"rateLimitExceeded\"}]}}", GoogleCalendarProblem.Unavailable, false)]
    [InlineData("not json", GoogleCalendarProblem.Reconnect, true)]
    public async Task AGoogleRefusalNamesItsCauseAndItsFix(string body, GoogleCalendarProblem problem, bool reconnect)
    {
        var google = new FakeGoogle { Refusal = body };
        var client = new GoogleCalendarClient(new HttpClient(google));
        var error = await Assert.ThrowsAsync<GoogleCalendarException>(() => client.InsertAsync(Connected, Entry, CancellationToken.None));
        Assert.Equal(problem, error.Problem);
        Assert.Equal(reconnect, error.RequiresReconnect);
        Assert.Empty(google.Events);
    }

    [Fact]
    public async Task MailGoesFromTheAccountToItselfAsEncodedPlainText()
    {
        var google = new FakeGoogle();
        var client = new GoogleCalendarClient(new HttpClient(google));
        var mailer = Connected with { CanMail = true };
        Assert.Equal("m1", await client.SendMailAsync(mailer, "demo@example.test", "Réserve à utiliser", "Ligne 1\nLigne é", CancellationToken.None));
        var message = Assert.Single(google.Mails);
        Assert.StartsWith("To: demo@example.test\r\nSubject: =?UTF-8?B?", message);
        Assert.Contains("Content-Type: text/plain; charset=UTF-8", message);
        var encoded = message[(message.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        Assert.Equal("Ligne 1\r\nLigne é", Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace("\r\n", ""))));
        // Never without the granted scope, never to an injected header.
        await Assert.ThrowsAsync<GoogleCalendarException>(() => client.SendMailAsync(Connected, "demo@example.test", "s", "b", CancellationToken.None));
        await Assert.ThrowsAsync<GoogleCalendarException>(() => client.SendMailAsync(mailer, "a@example.test\r\nBcc: x@example.test", "s", "b", CancellationToken.None));
        google.Refusal = "{\"error\":{\"errors\":[{\"reason\":\"accessNotConfigured\"}]}}";
        Assert.Equal(GoogleCalendarProblem.ApiDisabled, (await Assert.ThrowsAsync<GoogleCalendarException>(() => client.SendMailAsync(mailer, "demo@example.test", "s", "b", CancellationToken.None))).Problem);
        Assert.Single(google.Mails);
    }

    [Fact]
    public void TheMailScopeIsAskedOnlyWhenWanted()
    {
        var plain = GoogleCalendarClient.AuthorizationUri(Client, new("http://127.0.0.1:1/"), "c", "s").AbsoluteUri;
        var mail = GoogleCalendarClient.AuthorizationUri(Client, new("http://127.0.0.1:1/"), "c", "s", mail: true).AbsoluteUri;
        Assert.DoesNotContain(Uri.EscapeDataString(GoogleCalendarClient.MailScope), plain);
        Assert.Contains(Uri.EscapeDataString(GoogleCalendarClient.MailScope), mail);
    }

    [Fact]
    public async Task DeletingRemovesTheEventAndToleratesOneAlreadyGone()
    {
        var google = new FakeGoogle();
        var client = new GoogleCalendarClient(new HttpClient(google));
        await client.InsertAsync(Connected, Entry, CancellationToken.None);
        await client.DeleteAsync(Connected, CalendarExport.Key(Entry), CancellationToken.None);
        Assert.Empty(google.Events);
        await client.DeleteAsync(Connected, CalendarExport.Key(Entry), CancellationToken.None);
    }

    [Fact]
    public async Task AnExpiredAccessTokenIsRefreshedOnce()
    {
        var google = new FakeGoogle { RejectFirstAccess = true };
        var client = new GoogleCalendarClient(new HttpClient(google));
        await client.InsertAsync(Connected, Entry, CancellationToken.None);
        Assert.Equal(2, google.TokenRequests);
        Assert.Single(google.Events);
    }

    [Fact]
    public void SecretsAreEncryptedAndLinksPersistUntilAMonthAfterTheirDate()
    {
        using var directory = new TestDirectory();
        new GoogleCalendarSecretStore(directory.Root).Save(Connected);
        Assert.DoesNotContain("fake-refresh-token", Encoding.UTF8.GetString(File.ReadAllBytes(directory.File("google-calendar.dpapi"))));
        Assert.Equal(Connected, new GoogleCalendarSecretStore(directory.Root).Read());
        Assert.True(new GoogleCalendarSecretStore(directory.Root).Read().IsConnected);

        var now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");
        var links = new CalendarLinkStore(directory.Root);
        links.Put(new(CalendarExport.Key(Entry), CalendarLinkSource.Google, CalendarLinkStatus.Confirmed, Entry.StartsAt, now, "https://www.google.com/calendar/event?eid=x"));
        links.Put(new(new string('b', 64), CalendarLinkSource.Browser, CalendarLinkStatus.Opened, now.AddDays(-40), now));
        var reloaded = new CalendarLinkStore(directory.Root);
        Assert.Equal(CalendarLinkStatus.Confirmed, reloaded.Get(CalendarExport.Key(Entry))!.Status);
        reloaded.Prune(now);
        Assert.Null(new CalendarLinkStore(directory.Root).Get(new string('b', 64)));
        File.WriteAllText(directory.File("calendar-links.json"), "[{\"key\":\"short\"}]");
        Assert.Throws<InvalidDataException>(() => new CalendarLinkStore(directory.Root));
    }

    /// <summary>Token endpoint and the events of the primary calendar, in memory.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Events = [];
        public readonly List<string> Bodies = [], Methods = [], Mails = [];
        public int TokenRequests; public string? TokenError, Refusal; public bool RejectFirstAccess;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Bodies.Add(body); Methods.Add(request.Method.Method);
            var url = request.RequestUri!.AbsoluteUri;
            if (url == "https://oauth2.googleapis.com/token")
            {
                TokenRequests++;
                return TokenError is null ? Json(HttpStatusCode.OK, new { access_token = "access-" + TokenRequests, expires_in = 3600 })
                    : Json(HttpStatusCode.BadRequest, new { error = TokenError });
            }
            if (url == "https://gmail.googleapis.com/gmail/v1/users/me/messages/send")
            {
                if (Refusal is not null) return new(HttpStatusCode.Forbidden) { Content = new StringContent(Refusal, Encoding.UTF8, "application/json") };
                var raw = JsonDocument.Parse(body).RootElement.GetProperty("raw").GetString()!.Replace('-', '+').Replace('_', '/');
                Mails.Add(Encoding.UTF8.GetString(Convert.FromBase64String(raw.PadRight(raw.Length + (4 - raw.Length % 4) % 4, '='))));
                return Json(HttpStatusCode.OK, new { id = "m" + Mails.Count });
            }
            const string events = "https://www.googleapis.com/calendar/v3/calendars/primary/events";
            Assert.StartsWith(events, url);
            if (RejectFirstAccess && request.Headers.Authorization?.Parameter == "access-1") return new(HttpStatusCode.Unauthorized);
            if (Refusal is not null) return new(HttpStatusCode.Forbidden) { Content = new StringContent(Refusal, Encoding.UTF8, "application/json") };
            var id = url.Length > events.Length ? url[(events.Length + 1)..] : JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
            if (request.Method == HttpMethod.Post)
            {
                if (Events.ContainsKey(id)) return Json(HttpStatusCode.Conflict, new { error = new { code = 409 } });
                Events[id] = "confirmed";
            }
            else if (request.Method == HttpMethod.Put) Events[id] = JsonDocument.Parse(body).RootElement.GetProperty("status").GetString()!;
            else if (request.Method == HttpMethod.Delete) { if (!Events.Remove(id)) return Json(HttpStatusCode.Gone, new { error = new { code = 410 } }); return new(HttpStatusCode.NoContent); }
            else if (!Events.ContainsKey(id)) return Json(HttpStatusCode.NotFound, new { error = new { code = 404 } });
            return Json(HttpStatusCode.OK, new { id, status = Events[id], htmlLink = "https://www.google.com/calendar/event?eid=" + id });
        }
        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
