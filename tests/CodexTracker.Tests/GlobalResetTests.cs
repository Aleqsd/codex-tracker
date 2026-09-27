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
    [InlineData("We've reset usage limits in Codex for all paid users in the beta.")]
    [InlineData("We've reset usage limits in Codex for all paid users who signed up today.")]
    [InlineData("Someone said. We've reset usage limits in Codex for all paid users.")]
    [InlineData("We've reset usage limits in Codex for all paid users. Restrictions apply.")]
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
    public async Task ProductBeforeLimitsAndSpecificWindowRemainBounded()
    {
        var item = Assert.Single((await Read(new() { [Id(Now.AddMinutes(-2))] = "We've reset Codex 5-hour usage limits for all paid users. Enjoy!" })).Announcements);
        Assert.Equal([ResetKind.Short], item.Kinds); Assert.DoesNotContain("free", item.Plans);
    }

    [Fact]
    public void AccountReasonsDistinguishObservationAndEligibilityWithoutImplyingAConfirmedReset()
    {
        var a = Announcement; var account = Account();
        Assert.Equal(GlobalResetAccountStatus.Estimated, a.StatusFor(account, ResetKind.Weekly, Now));
        Assert.Equal(GlobalResetAccountStatus.NotCovered, a.StatusFor(Account("free"), ResetKind.Weekly, Now));
        Assert.Equal(GlobalResetAccountStatus.InsufficientData, a.StatusFor(Account(null), ResetKind.Weekly, Now));
        Assert.Equal(GlobalResetAccountStatus.Active, a.StatusFor(account with { IsActiveInCodex = true }, ResetKind.Weekly, Now));
        Assert.Equal(GlobalResetAccountStatus.NewerObservation, a.StatusFor(account with { Snapshot = account.Snapshot! with { FetchedAt = a.AnnouncedAt } }, ResetKind.Weekly, Now));
        Assert.Equal(GlobalResetAccountStatus.Expired, a.StatusFor(account, ResetKind.Short, Now));
    }

    private sealed class ControlledReader : IGlobalResetReader
    {
        public int Calls;
        public Exception? Failure;
        public TaskCompletionSource? Hold;
        public CancellationToken LastToken;
        public async Task<GlobalResetFeedState> ReadAsync(DateTimeOffset now, CancellationToken token = default)
        {
            Calls++; LastToken = token;
            if (Hold is { } hold) await hold.Task; // Simulates a transport completing after cancellation.
            if (Failure is { } error) throw error;
            return new([Announcement], now);
        }
    }
    [Fact]
    public void DesktopSummaryCountsAccountsInsteadOfWindowsAndKeepsOtherRemindersVisible()
    {
        var accounts = new[] { Account(), Account(), Account() };
        var fresh = Announcement with { ReportedAt = Now.AddHours(-1) };
        var rows = ExpectedReset.Due(new(accounts, null) { GlobalResetFeed = new([fresh]) }, Now);
        Assert.Equal(6, rows.Count);
        var summary = DesktopReminderText.For(rows);
        Assert.Contains("3 comptes", summary.Title); Assert.Contains("(+1)", summary.Body);
        Assert.Contains("probablement", summary.Body); Assert.Contains("relevés datés", summary.Body);
        Assert.DoesNotContain("6 événements", summary.Title);
        var other = rows[0] with { Key = "ordinary-reminder", LeadMinutes = 60 };
        Assert.Contains("D’autres rappels", DesktopReminderText.For([.. rows, other]).Body);
        Assert.Equal(ReminderPlanner.Body(other), DesktopReminderText.For([other]).Body);
    }
    [Fact]
    public async Task RapidDisableReenableAndConcurrentChecksCannotRestoreAnOldResponse()
    {
        bool enabled = true; var reader = new ControlledReader { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var monitor = new GlobalResetMonitor(reader, () => enabled);
        var first = monitor.CheckAsync(Now); Assert.True(monitor.State!.IsChecking);
        await monitor.CheckAsync(Now, manual: true); Assert.Equal(1, reader.Calls);
        enabled = false; monitor.Synchronize(); Assert.True(reader.LastToken.IsCancellationRequested); Assert.Null(monitor.State);
        enabled = true; monitor.Synchronize(); reader.Hold.SetResult(); await first;
        Assert.Empty(monitor.State!.Announcements); Assert.False(monitor.State.IsChecking);
        reader.Hold = null; await monitor.CheckAsync(Now.AddSeconds(1)); Assert.Empty(monitor.State!.Announcements);
        await monitor.CheckAsync(Now.AddMinutes(1)); Assert.Single(monitor.State!.Announcements);
        Assert.Equal(2, reader.Calls);
    }
    [Fact]
    public async Task ManualChecksBypassRegularScheduleButRespectCooldownAndRateLimit()
    {
        var reader = new ControlledReader(); var monitor = new GlobalResetMonitor(reader, () => true);
        await monitor.CheckAsync(Now); Assert.Equal(Now.AddMinutes(15), monitor.State!.NextCheckAt);
        await monitor.CheckAsync(Now.AddSeconds(59), manual: true); Assert.Equal(1, reader.Calls);
        await monitor.CheckAsync(Now.AddMinutes(1), manual: true); Assert.Equal(2, reader.Calls);
        reader.Failure = new HttpRequestException("Do not show raw provider details", null, HttpStatusCode.TooManyRequests);
        await monitor.CheckAsync(Now.AddMinutes(2), manual: true);
        Assert.Equal(Now.AddMinutes(32), monitor.State!.ManualRetryAt);
        await monitor.CheckAsync(Now.AddMinutes(31), manual: true); Assert.Equal(3, reader.Calls);
        Assert.DoesNotContain("provider", monitor.State.Error);
    }
    [Fact]
    public async Task TogglingOrSleepingCannotBypassAProviderCooldown()
    {
        bool enabled = true;
        var reader = new ControlledReader { Failure = new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests) };
        var monitor = new GlobalResetMonitor(reader, () => enabled); await monitor.CheckAsync(Now);
        monitor.Suspend(); await monitor.CheckAsync(Now.AddMinutes(1)); Assert.Equal(1, reader.Calls);
        enabled = false; monitor.Synchronize(); enabled = true; monitor.Synchronize();
        await monitor.CheckAsync(Now.AddMinutes(1), manual: true); Assert.Equal(1, reader.Calls);
        await monitor.CheckAsync(Now.AddMinutes(30), manual: true); Assert.Equal(2, reader.Calls);
    }
    [Theory]
    [InlineData(404)] [InlineData(410)] [InlineData(401)] [InlineData(403)]
    public async Task WithdrawnOrUnavailableOriginalRemovesEstimates(int status)
    {
        var reader = new ControlledReader(); var monitor = new GlobalResetMonitor(reader, () => true);
        await monitor.CheckAsync(Now); Assert.Single(monitor.State!.Announcements);
        reader.Failure = new HttpRequestException("private transport details", null, (HttpStatusCode)status);
        await monitor.CheckAsync(Now.AddMinutes(1), manual: true);
        Assert.Empty(monitor.State!.Announcements); Assert.Contains("retirées", monitor.State.Error);
        Assert.Equal(Now, monitor.State.CheckedAt); Assert.Equal(Now.AddMinutes(1), monitor.State.LastAttemptAt);
    }
    [Fact]
    public async Task SuspendCancelsRequestAndResumeCanCheckAgain()
    {
        var reader = new ControlledReader { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var monitor = new GlobalResetMonitor(reader, () => true); var pending = monitor.CheckAsync(Now);
        monitor.Suspend(); Assert.True(reader.LastToken.IsCancellationRequested);
        reader.Hold.SetResult(); await pending; Assert.Empty(monitor.State!.Announcements);
        reader.Hold = null; await monitor.CheckAsync(Now.AddMinutes(1)); Assert.Single(monitor.State!.Announcements);
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
        bool enabled = true; var monitor = new GlobalResetMonitor(new GlobalResetReader(http), () => enabled);
        await monitor.CheckAsync(Now); var calls = handler.Calls;
        await monitor.CheckAsync(Now.AddMinutes(14)); Assert.Equal(calls, handler.Calls);
        handler.Fail = true; await monitor.CheckAsync(Now.AddMinutes(15));
        Assert.NotNull(monitor.State!.Error); Assert.Equal(Now, monitor.State.CheckedAt); Assert.Single(monitor.State.Announcements);
        calls = handler.Calls; await monitor.CheckAsync(Now.AddMinutes(44)); Assert.Equal(calls, handler.Calls);
        enabled = false; Assert.Null(monitor.State); await monitor.CheckAsync(Now.AddHours(1)); Assert.Equal(calls, handler.Calls);
        enabled = true; handler.Fail = false; await monitor.CheckAsync(Now.AddMinutes(45)); Assert.Null(monitor.State!.Error);

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slow = new HttpClient(new Handler(ReviewedPair()) { Hold = hold });
        var late = new GlobalResetMonitor(new GlobalResetReader(slow), () => enabled); var request = late.CheckAsync(Now);
        enabled = false; hold.SetResult(); await request; enabled = true;
        Assert.Empty(late.State!.Announcements);
    }
}
