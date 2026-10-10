using CodexTracker.App;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class AccountLinkTests
{
    private static readonly Guid Account = Guid.Parse("5fc02712-4d95-4669-8a31-a362b52a82e0");

    [Theory]
    [InlineData("codextracker://account/5fc02712-4d95-4669-8a31-a362b52a82e0")]
    [InlineData("codextracker://account/5fc02712-4d95-4669-8a31-a362b52a82e0/")]
    [InlineData("  CODEXTRACKER://Account/5FC02712-4D95-4669-8A31-A362B52A82E0  ")]
    public void CalendarLinksNameOneAccount(string link) => Assert.Equal(Account, AccountLink.Parse(link));

    [Theory]
    [InlineData("codextracker://account/not-a-guid")]
    [InlineData("codextracker://settings/5fc02712-4d95-4669-8a31-a362b52a82e0")]
    [InlineData("https://account/5fc02712-4d95-4669-8a31-a362b52a82e0")]
    [InlineData("--background")]
    [InlineData("")]
    public void OtherArgumentsAreNotLinks(string value) => Assert.Null(AccountLink.Parse(value));

    [Fact]
    public void TheAccountSurvivesTheSingleInstanceMessage()
    {
        Assert.Equal(Account, AccountLink.Parse(["--background", CalendarExport.AccountLink(Account)]));
        var (first, second) = AccountLink.Pack(Account);
        Assert.Equal(Account, AccountLink.Unpack(first, second));
        Assert.Equal(Guid.Empty, AccountLink.Unpack(IntPtr.Zero, IntPtr.Zero));
    }

    [Fact]
    public void EveryCalendarEventLinksToItsAccount()
    {
        var now = DateTimeOffset.Parse("2026-10-10T08:00:00Z");
        var state = new TrackerState([new(new(Account, "studio@example.test"), new("studio@example.test", "plus",
            [new("codex", null, [new(40, 10080, now.AddDays(2)), new(10, 300, now.AddHours(2))])], 1, [new("credit-A", "Réserve", now.AddDays(-1), now.AddDays(1))], now.AddMinutes(-5)))], Account);
        var entries = CalendarExport.Entries(state, p => p.Email, now);
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => { Assert.Equal(CalendarExport.AccountLink(Account), e.Link); Assert.Contains(e.Link!, e.Description); });
        var ics = CalendarExport.Serialize(entries, now);
        Assert.Equal(3, ics.Split("\r\n").Count(line => line == "URL:codextracker://account/" + Account.ToString("D")));
        Assert.Contains(Uri.EscapeDataString(CalendarExport.AccountLink(Account)), CalendarExport.GoogleEventLink(entries[0]).OriginalString);
    }
}
