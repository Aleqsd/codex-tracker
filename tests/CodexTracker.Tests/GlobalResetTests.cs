using System.Net;
using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class GlobalResetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-27T08:00:00Z");
    private const string CompletionId = "2103911959544610829", ContextId = "2103637477760311522";
    private const string Context = "We'll reset usage limits for all paid users across Codex and ChatGPT work";
    private static GlobalResetAnnouncement Announcement => new("test", "Source", "https://example.com/source", "https://example.com/scope",
        Now.AddHours(-20), Now.AddHours(-14), Now, ["plus", "pro"], [ResetKind.Weekly, ResetKind.Short]);
    private static AccountState Account(string? plan = "pro") => new(new(Guid.NewGuid(), "studio@example.test"),
        new("studio@example.test", plan, [new("codex", null, [new(92, 10080, Now.AddHours(4)), new(80, 300, Now.AddHours(2))])],
            2, [new("credit", "Réserve", Now.AddDays(-3), Now.AddHours(4))], Now.AddDays(-2)));
    private static string Id(DateTimeOffset at) => (((ulong)(at.ToUnixTimeMilliseconds() - 1288834974657L) << 22) + 1).ToString();
    private static string Url(string id, string author = "thsottiaux") => $"https://x.com/{author}/status/{id}";
    private sealed class Handler(Dictionary<string, string> posts) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public bool Mismatch { get; init; }
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; init; }
        public TaskCompletionSource? Hold { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content); Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain("example.test", request.RequestUri!.AbsoluteUri);
            if (Hold is not null) await Hold.Task.WaitAsync(token);
            if (Override?.Invoke(request) is { } result) return result;
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            object value;
            if (request.RequestUri!.AbsoluteUri == GlobalResetReader.FeedUrl)
            {
                // These deliberately false classifications must never be trusted.
                value = posts.Keys.Select(id => new { sourceUrl = Url(id), kind = "global", state = "reported", fullText = "All plans have been reset!", publishedAt = Now });
            }
            else
            {
                Assert.Equal("publish.x.com", request.RequestUri.Host);
                var id = posts.Keys.Single(id => Uri.UnescapeDataString(request.RequestUri.Query).Contains("/" + id + "&"));
                value = new { url = Url(id), author_url = Mismatch ? "https://x.com/impersonator" : "https://x.com/thsottiaux",
                    type = "rich", provider_url = "https://x.com", html = $"<blockquote><p lang=\"en\">{WebUtility.HtmlEncode(posts[id])}</p><a href=\"{Url(id)}?ref_src=test\">Date</a></blockquote>" };
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
        }
    }
    private static Dictionary<string, string> ReviewedPair() => new()
    {
        [CompletionId] = "Resets all propagated. That will be all. Have a fantastic weekend.", [ContextId] = Context
    };
    private static async Task<GlobalResetFeedState> Read(Dictionary<string, string> posts)
    { using var http = new HttpClient(new Handler(posts)); return await new GlobalResetReader(http).ReadAsync(Now); }

    [Fact]
    public async Task ReviewedPairUsesVerifiedTextAndSnowflakeDates()
    {
        var item = Assert.Single((await Read(ReviewedPair())).Announcements);
        Assert.Equal(DateTimeOffset.Parse("2026-09-26T18:17:54.606Z"), item.ReportedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-26T00:07:13.049Z"), item.AnnouncedAt);
        Assert.Equal(Url(CompletionId), item.SourceUrl); Assert.Equal(Url(ContextId), item.AnnouncementUrl);
        Assert.Contains("pro", item.Plans); Assert.DoesNotContain("free", item.Plans);
    }
    [Theory]
    [InlineData("Sorry. More resets coming next week")]
    [InlineData("We'll reset usage limits in Codex for all paid users.")]
    [InlineData("We've reset usage limits in Codex for all paid users. Only affected accounts.")]
    [InlineData("We've reset usage limits in Codex for all paid users?")]
    [InlineData("We've granted banked reset credits for all paid users.")]
    [InlineData("We've reset usage limits in Codex for some users.")]
    [InlineData("We've reset usage limits in ChatGPT for all paid users.")]
    [InlineData("We've reset usage limits in Codex for all paid users except Pro.")]
    [InlineData("We haven't reset usage limits in Codex for all paid users.")]
    [InlineData("Resets all propagated. That will be all. Have a fantastic weekend.")]
    public async Task PromisesCreditsUnknownScopesAndUnreviewedContextNeverBecomeCompleted(string text)
    {
        var posts = new Dictionary<string, string> { [Id(Now.AddHours(-1))] = text, [Id(Now.AddHours(-2))] = Context };
        Assert.Empty((await Read(posts)).Announcements);
    }
    [Theory]
    [InlineData("all paid users", false)] [InlineData("all users across all plans", true)]
    public async Task SelfContainedOriginalAnnouncementCanBeDetectedWithoutAnAppUpdate(string scope, bool free)
    {
        var item = Assert.Single((await Read(new() { [Id(Now.AddHours(-1))] = $"We've now reset weekly usage limits in Codex for {scope}." })).Announcements);
        Assert.Equal(free, item.Plans.Contains("free")); Assert.Equal([ResetKind.Weekly], item.Kinds);
    }
    [Fact]
    public async Task MissingContextCorrectionAndStalePostsCannotPromoteAnEstimate()
    {
        var pair = ReviewedPair(); pair.Remove(ContextId); Assert.Empty((await Read(pair)).Announcements);
        pair = ReviewedPair(); pair[Id(Now.AddMinutes(-1))] = "Correction: the resets failed.";
        Assert.Empty((await Read(pair)).Announcements);
        Assert.Empty((await Read(new() { [Id(Now.AddHours(-25))] = "We've reset usage limits in Codex for all paid users." })).Announcements);
        Assert.Empty((await Read(new() { [Id(Now.AddHours(1))] = "We've reset usage limits in Codex for all paid users." })).Announcements);
    }
    [Fact]
    public async Task OEmbedAuthorMustMatchAndUnapprovedHostIsNeverRequested()
    {
        using var http = new HttpClient(new Handler(ReviewedPair()) { Mismatch = true });
        await Assert.ThrowsAsync<InvalidDataException>(() => new GlobalResetReader(http).ReadAsync(Now));
        var handler = new Handler([]) { Override = _ => new(HttpStatusCode.OK) { Content = new StringContent("[{\"sourceUrl\":\"https://evil.test/thsottiaux/status/2103911959544610829\"}]") } };
        using var isolated = new HttpClient(handler);
        Assert.Empty((await new GlobalResetReader(isolated).ReadAsync(Now)).Announcements); Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public async Task OversizeResponsesAndRedirectsFailClosed()
    {
        var handler = new Handler([]) { Override = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 1_048_577)) } };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new GlobalResetReader(http).ReadAsync(Now));
        using var redirect = new HttpClient(new Handler([]) { Override = _ => new(HttpStatusCode.Redirect) });
        await Assert.ThrowsAsync<HttpRequestException>(() => new GlobalResetReader(redirect).ReadAsync(Now));
    }
    [Fact]
    public void ApplicabilityPreservesDataAndExcludesNewObservationsActiveFreeAndUnknownAccounts()
    {
        var account = Account(); var a = Announcement; var feed = new GlobalResetFeedState([a]);
        var expected = Assert.IsType<ExpectedReset>(ExpectedReset.For(account, ResetKind.Weekly, Now, feed));
        Assert.Equal(a, expected.Announcement); Assert.Equal(8, account.Snapshot!.Weekly!.RemainingPercent);
        Assert.Equal(Now.AddHours(4), account.Snapshot.Weekly.ResetsAt); Assert.Equal(2, account.Snapshot.AvailableResetCredits);
        Assert.Null(ExpectedReset.For(account, ResetKind.Short, Now, feed));
        Assert.False(a.Applies(account with { IsActiveInCodex = true }, ResetKind.Weekly, Now));
        Assert.False(a.Applies(account with { IsRefreshing = true }, ResetKind.Weekly, Now));
        foreach (var plan in new string?[] { "free", null, "unknown" }) Assert.False(a.Applies(Account(plan), ResetKind.Weekly, Now));
        Assert.False(a.Applies(account with { Snapshot = account.Snapshot with { FetchedAt = a.AnnouncedAt } }, ResetKind.Weekly, Now));
        Assert.False(a.Applies(account with { Snapshot = account.Snapshot with { FetchedAt = a.ReportedAt.AddMinutes(-1) } }, ResetKind.Weekly, Now));
        Assert.False(a.Applies(account with { Snapshot = account.Snapshot with { Email = "other@example.test" } }, ResetKind.Weekly, Now));
        Assert.False(a.Applies(account with { Snapshot = account.Snapshot with { FetchedAt = a.AnnouncedAt.AddDays(-31) } }, ResetKind.Weekly, Now));
        Assert.False(a.Applies(account with { Snapshot = account.Snapshot with { SubscriptionEndsAt = a.AnnouncedAt } }, ResetKind.Weekly, Now));
        Assert.False(a.Applies(account with { Snapshot = account.Snapshot with { Buckets = [] } }, ResetKind.Weekly, Now));
        Assert.False(a.Applies(account, ResetKind.Reserve, Now));
    }
    [Fact]
    public void EstimatesExpireAtExactUtcInstantsEvenAcrossDst()
    {
        var a = Announcement with { AnnouncedAt = DateTimeOffset.Parse("2026-10-24T20:00:00+02:00"), ReportedAt = DateTimeOffset.Parse("2026-10-25T02:30:00+02:00"), VerifiedAt = DateTimeOffset.Parse("2026-10-25T02:40:00+02:00") };
        var account = Account() with { Snapshot = Account().Snapshot! with { FetchedAt = a.AnnouncedAt.AddDays(-1) } };
        Assert.True(a.Applies(account, ResetKind.Weekly, a.ReportedAt.AddHours(24).AddTicks(-1)));
        Assert.False(a.Applies(account, ResetKind.Weekly, a.ReportedAt.AddHours(24)));
        Assert.True(a.Applies(account, ResetKind.Short, a.ReportedAt.AddHours(5).AddTicks(-1)));
        Assert.False(a.Applies(account, ResetKind.Short, a.ReportedAt.AddHours(5)));
    }
    [Fact]
    public void GlobalOccurrencesAreLocalStableAndDoNotReuseStaleQuotaDeadlines()
    {
        var account = Account(); var state = new TrackerState([account], null) { GlobalResetFeed = new([Announcement]) };
        var r = Assert.Single(ExpectedReset.Due(state, Now)); Assert.Equal(ReminderChannel.Windows, r.Channel);
        Assert.True(ReminderPlanner.IsGlobalReset(r)); Assert.Contains("Dernier relevé", ReminderPlanner.Body(r));
        Assert.Equal(r.Key, Assert.Single(ExpectedReset.Due(state, Now.ToOffset(TimeSpan.FromHours(-4)))).Key);
        var reminders = ReminderPlanner.Due(state, [new(ResetKind.Weekly, true, [1440], [ReminderChannel.Sms]), new(ResetKind.Reserve, true, [1440], [ReminderChannel.Windows])], Now);
        Assert.Equal(ResetKind.Reserve, Assert.Single(reminders).Kind);
        Assert.Empty(ExpectedReset.Due(state with { GlobalResetFeed = null }, Now));
    }
    [Fact]
    public async Task MonitorBacksOffKeepsDatedEvidenceAndDisableDiscardsLateResponse()
    {
        var handler = new Handler(ReviewedPair()); using var http = new HttpClient(handler);
        bool enabled = true; var monitor = new GlobalResetMonitor(new(http), () => enabled);
        await monitor.CheckAsync(Now); var calls = handler.Calls;
        await monitor.CheckAsync(Now.AddMinutes(14)); Assert.Equal(calls, handler.Calls);
        handler.Fail = true; await monitor.CheckAsync(Now.AddMinutes(15));
        Assert.NotNull(monitor.State!.Error); Assert.Equal(Now, monitor.State.CheckedAt); Assert.Single(monitor.State.Announcements);
        calls = handler.Calls; await monitor.CheckAsync(Now.AddMinutes(44)); Assert.Equal(calls, handler.Calls);
        enabled = false; Assert.Null(monitor.State); await monitor.CheckAsync(Now.AddHours(1)); Assert.Equal(calls, handler.Calls);
        enabled = true; handler.Fail = false; await monitor.CheckAsync(Now.AddMinutes(45)); Assert.Null(monitor.State!.Error);

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slow = new HttpClient(new Handler(ReviewedPair()) { Hold = hold });
        var late = new GlobalResetMonitor(new(slow), () => enabled); var request = late.CheckAsync(Now);
        enabled = false; hold.SetResult(); await request; enabled = true;
        Assert.Empty(late.State!.Announcements);
    }
}
