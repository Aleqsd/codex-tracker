using System.Net;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class NewResetCreditTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");
    private static readonly Guid AccountId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static TrackerState State(params ResetCredit[] credits) => State(AccountProvider.Codex, credits);
    private static TrackerState State(AccountProvider provider, params ResetCredit[] credits) => new([
        new(new(AccountId, "studio@example.test", provider), new("studio@example.test", "plus", [new("codex", null, [new(50, 10080, Now.AddDays(3))])],
            credits.Length, credits, Now.AddMinutes(-3)), IsActiveInCodex: true)], AccountId);
    private static ResetCredit Credit(string id, TimeSpan grantedAgo, TimeSpan expiresIn) => new(id, "Full reset", Now - grantedAgo, Now + expiresIn);

    [Fact]
    public void OnlyRecentGrantedCreditsWithAFutureExpiryAreAnnounced()
    {
        var state = State(Credit("new", TimeSpan.FromHours(14), TimeSpan.FromDays(29)), Credit("old", TimeSpan.FromDays(20), TimeSpan.FromDays(10)),
            Credit("expired", TimeSpan.FromHours(1), TimeSpan.FromMinutes(-1)), new("undated", null, null, Now.AddDays(5)),
            Credit("future", TimeSpan.FromHours(-1), TimeSpan.FromDays(30)));
        var due = Assert.Single(NewResetCredits.Due(state, Now));
        Assert.Equal("new", due.CreditId);
        Assert.True(NewResetCredits.Is(due));
        Assert.Equal(ResetKind.Reserve, due.Kind); Assert.Equal(0, due.LeadMinutes); Assert.Equal(ReminderChannel.Windows, due.Channel);
        Assert.Equal(CalendarExport.Identity(AccountId, "credit", "new", Now.AddDays(29)), NewResetCredits.CalendarIdentity(due));
        Assert.Equal("Nouveau reset en réserve", ReminderPlanner.Title(due));
        Assert.Contains("À utiliser avant le", ReminderPlanner.Body(due));
        Assert.Empty(NewResetCredits.Due(State(AccountProvider.ClaudeCode, Credit("claude", TimeSpan.FromHours(1), TimeSpan.FromDays(3))), Now));
        Assert.Empty(NewResetCredits.Due(State() with { Accounts = [state.Accounts[0] with { Snapshot = state.Accounts[0].Snapshot! with { AvailableResetCredits = 0 } }] }, Now));
    }

    [Fact]
    public async Task ANewCreditIsNotifiedOnceAndKeepsItsExpiryReminders()
    {
        using var directory = new TestDirectory();
        using var http = new HttpClient(new Refuse());
        var clock = new Clock { Now = Now };
        var desktop = new List<IReadOnlyList<ReminderOccurrence>>();
        var enabled = true;
        // Expires in 2 days: the 3-day reserve reminder is due together with the new-credit notice.
        var state = State(Credit("new", TimeSpan.FromHours(2), TimeSpan.FromDays(2)));
        ReminderRule[] rules = [new(ResetKind.Reserve, true, [4320, 1440], [ReminderChannel.Windows])];
        ReminderDispatcher Create() => new(new ReminderJournal(directory.Root), new NotificationSecretStore(directory.Root), new(http), () => state, () => rules,
            () => new(), () => new Dictionary<string, DateTimeOffset>(), rows => { desktop.Add(rows); return new(DeliveryStatus.Accepted, "ok"); }, clock,
            newCreditNotifications: () => enabled);
        var dispatcher = Create();
        await dispatcher.TickAsync();
        var shown = Assert.Single(desktop);
        Assert.Contains(shown, NewResetCredits.Is);
        Assert.Contains(shown, r => r.LeadMinutes == 4320);
        Assert.StartsWith(ReminderPlanner.Body(shown.First(NewResetCredits.Is)), DesktopReminderText.For(shown).Body);

        // Restart, then the 1-day reminder: the notice is not replayed and does not swallow the reminder.
        dispatcher = Create(); clock.Now = Now.AddDays(1).AddMinutes(1);
        await dispatcher.TickAsync();
        Assert.Equal(2, desktop.Count);
        Assert.Equal(1440, Assert.Single(desktop[1]).LeadMinutes);
        await dispatcher.TickAsync();
        Assert.Equal(2, desktop.Count);

        // Disabled: nothing for a second credit.
        enabled = false; state = State(Credit("other", TimeSpan.FromHours(1), TimeSpan.FromDays(20)));
        await Create().TickAsync();
        Assert.Equal(2, desktop.Count);
    }

    [Fact]
    public async Task ADayBeforeACreditIsLostAnEmailGoesThroughGmailOnce()
    {
        using var directory = new TestDirectory();
        using var http = new HttpClient(new Refuse());
        var clock = new Clock { Now = Now };
        var mails = new List<ReminderOccurrence>();
        var granted = true;
        // Granted 20 days ago, expires in 20 hours: inside the default 24-hour email alert.
        var state = State(Credit("old", TimeSpan.FromDays(20), TimeSpan.FromHours(20)));
        ReminderDispatcher Create() => new(new ReminderJournal(directory.Root), new NotificationSecretStore(directory.Root), new(http), () => state, () => [],
            () => new(), () => new Dictionary<string, DateTimeOffset>(), _ => new(DeliveryStatus.Accepted, "ok"), clock,
            gmail: (r, _) => { mails.Add(r); return Task.FromResult(new DeliveryResult(DeliveryStatus.Delivered, "sent")); }, gmailReady: () => granted, reserveMail: () => true);
        await Create().TickAsync();
        var mail = Assert.Single(mails);
        Assert.Equal(ReminderChannel.Email, mail.Channel); Assert.Equal(1440, mail.LeadMinutes); Assert.Equal("old", mail.CreditId);
        await Create().TickAsync();
        Assert.Single(mails);
        // Without Gmail sending granted, nothing is planned or journaled for email.
        granted = false; state = State(Credit("other", TimeSpan.FromDays(10), TimeSpan.FromHours(5)));
        var dispatcher = Create(); await dispatcher.TickAsync();
        Assert.Single(mails);
        Assert.DoesNotContain(dispatcher.History, d => d.Occurrence.CreditId == "other" && d.Occurrence.Channel == ReminderChannel.Email);
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("No external request expected.");
    }
}
