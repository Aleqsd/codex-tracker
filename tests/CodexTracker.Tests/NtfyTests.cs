using System.Net;
using System.Text.Json;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class NtfyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");
    private static readonly Guid AccountId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string Topic = "codex-tracker-0123456789abcdef";
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Ntfy(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Auth, JsonElement Body)> Posts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement.Clone();
            Posts.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return new(status) { Content = new StringContent("{\"id\":\"abc\",\"event\":\"message\"}") };
        }
    }

    [Theory]
    [InlineData("https://ntfy.sh", Topic, true)]
    [InlineData("https://push.example.test/ntfy/", Topic, true)]
    [InlineData("http://ntfy.sh", Topic, false)]           // https only
    [InlineData("https://ntfy.sh?x=1", Topic, false)]      // no query
    [InlineData("https://ntfy.sh", "short", false)]        // guessable topic
    [InlineData("https://ntfy.sh", "has space in it 12", false)]
    [InlineData("not a url", Topic, false)]
    public void OnlyAnHttpsServerAndALongTopicAreAccepted(string server, string topic, bool valid)
    {
        var settings = new NtfySettings(true, server, topic);
        Assert.Equal(valid, NotificationProviders.PushReady(settings));
        Assert.Equal(valid, NotificationProviders.Configured(ReminderChannel.Push, NotificationSecrets.Empty with { Ntfy = settings }));
        Assert.False(NotificationProviders.Configured(ReminderChannel.Push, NotificationSecrets.Empty with { Ntfy = settings with { Enabled = false } }));
    }

    [Fact]
    public void NewTopicsAreLongRandomAndValid()
    {
        var a = NtfySettings.NewTopic(); var b = NtfySettings.NewTopic();
        Assert.NotEqual(a, b);
        Assert.Equal("https://ntfy.sh/" + a, NotificationProviders.SubscribeUrl(new(true, "https://ntfy.sh/", a)));
        Assert.True(a.Length >= 30);
    }

    [Fact]
    public async Task APushIsOneJsonPublishToTheServerRootWithTheTokenWhenGiven()
    {
        var handler = new Ntfy();
        var providers = new NotificationProviders(new HttpClient(handler));
        var result = await providers.SendPushAsync(new(true, "https://push.example.test/ntfy", Topic, "tk_secret"), "Quota bientôt épuisé", "Il reste 10 %", default);
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        var post = Assert.Single(handler.Posts);
        Assert.Equal("https://push.example.test/ntfy/", post.Uri.ToString());
        Assert.Equal("Bearer tk_secret", post.Auth);
        Assert.Equal(Topic, post.Body.GetProperty("topic").GetString());
        Assert.Equal("Quota bientôt épuisé", post.Body.GetProperty("title").GetString());
        Assert.Equal("Il reste 10 %", post.Body.GetProperty("message").GetString());

        var failing = new NotificationProviders(new HttpClient(new Ntfy(HttpStatusCode.BadGateway)));
        Assert.Equal(DeliveryStatus.Unknown, (await failing.SendPushAsync(new(true, "https://ntfy.sh", Topic), "t", "b", default)).Status);
        var refused = new NotificationProviders(new HttpClient(new Ntfy(HttpStatusCode.Forbidden)));
        Assert.Equal(DeliveryStatus.Failed, (await refused.SendPushAsync(new(true, "https://ntfy.sh", Topic), "t", "b", default)).Status);
    }

    [Fact]
    public void SecretsWrittenBeforeThePhoneChannelStillLoad()
    {
        using var directory = new TestDirectory();
        var store = new NotificationSecretStore(directory.Root);
        store.Save(new NotificationSecrets(new(), new()));
        var loaded = store.Read();
        Assert.Null(loaded.Ntfy);
        Assert.False(loaded.Push.Enabled);
        store.Save(loaded with { Ntfy = new(true, "https://ntfy.sh", Topic) });
        Assert.Equal(Topic, store.Read().Push.Topic);
    }

    private static TrackerState State() => new([
        new(new(AccountId, "studio@example.test"), new("studio@example.test", "plus", [new("codex", null, [new(50, 10080, Now.AddMinutes(50))])],
            0, [], Now.AddMinutes(-3)), IsActiveInCodex: true)], AccountId);

    [Fact]
    public async Task WindowsRemindersAreCopiedToThePhoneOnceAndOnlyWhenTheCopyIsOn()
    {
        using var directory = new TestDirectory();
        var handler = new Ntfy();
        var secrets = new NotificationSecretStore(directory.Root);
        secrets.Save(NotificationSecrets.Empty with { Ntfy = new(true, "https://ntfy.sh", Topic) });
        var mirror = true; var shown = 0;
        ReminderRule[] rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Windows])];
        var clock = new Clock { Now = Now };
        ReminderDispatcher Create() => new(new ReminderJournal(directory.Root), secrets, new(new HttpClient(handler)), State, () => rules, () => new(),
            () => new Dictionary<string, DateTimeOffset>(), _ => { shown++; return new(DeliveryStatus.Shown, "ok"); }, clock, pushMirror: () => mirror);

        var dispatcher = Create();
        await dispatcher.TickAsync();
        Assert.Equal(1, shown);
        var post = Assert.Single(handler.Posts);
        Assert.Equal(Topic, post.Body.GetProperty("topic").GetString());
        Assert.Contains(dispatcher.History, d => d.Occurrence.Channel == ReminderChannel.Push && d.Status == DeliveryStatus.Accepted);

        // A restart replays nothing.
        await Create().TickAsync();
        Assert.Single(handler.Posts);

        // Quota alerts shown outside the engine: once per key, nothing when the copy is off.
        dispatcher = Create();
        Assert.NotNull(await dispatcher.MirrorAsync("alert/x/10", "Quota bientôt épuisé", "Il reste 10 %"));
        Assert.Null(await dispatcher.MirrorAsync("alert/x/10", "Quota bientôt épuisé", "Il reste 10 %"));
        mirror = false;
        Assert.Null(await dispatcher.MirrorAsync("alert/x/5", "Quota bientôt épuisé", "Il reste 5 %"));
        Assert.Equal(2, handler.Posts.Count);
    }

    [Fact]
    public async Task APhoneRuleSendsThroughNtfyWithoutTheCopy()
    {
        using var directory = new TestDirectory();
        var handler = new Ntfy();
        var secrets = new NotificationSecretStore(directory.Root);
        secrets.Save(NotificationSecrets.Empty with { Ntfy = new(true, "https://ntfy.sh", Topic) });
        ReminderRule[] rules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Push])];
        var dispatcher = new ReminderDispatcher(new ReminderJournal(directory.Root), secrets, new(new HttpClient(handler)), State, () => rules, () => new(),
            () => new Dictionary<string, DateTimeOffset>(), _ => new(DeliveryStatus.Shown, "ok"), new Clock { Now = Now });
        await dispatcher.TickAsync();
        var post = Assert.Single(handler.Posts);
        Assert.Contains("studio@example.test", post.Body.GetProperty("message").GetString() + post.Body.GetProperty("title").GetString());
        Assert.Equal(DeliveryStatus.Accepted, (await dispatcher.TestAsync(ReminderChannel.Push)).Status);
    }
}
