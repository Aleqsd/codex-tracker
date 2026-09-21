using System.Text;
using CodexTracker.App;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class PersonalizationAndCalendarTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T10:00:00Z");
    private static readonly AccountProfile Profile = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "private@example.test");
    private static AccountSnapshot Snapshot(int? available = 2) => new(Profile.Email, "plus",
        [new("codex", "Codex", [new(20, 10080, Now.AddDays(2)), new(10, 300, Now.AddHours(2))])], available,
        [new("credit-1", "Private title", Now.AddDays(-20), Now.AddHours(23)), new("unknown", null, null, null), new("expired", null, null, Now.AddMinutes(-1))], Now.AddHours(-4));
    private static TrackerState State(AccountSnapshot? snapshot) => new([new(Profile, snapshot)], Profile.Id);

    [Fact]
    public void PersonalizationAndSchedulingSurviveRestartWithoutChangingIdentity()
    {
        using var directory = new TestDirectory(); var store = new PreferencesStore(dataDirectory: directory.Root);
        store.Update(p => p with { RefreshMinutes = 5, AdaptiveRefresh = true, ExpiryLeadHours = 72,
            Appearances = new() { [Profile.Id] = new("Travail", "11111111111111111111111111111111.png") } });
        var restarted = new PreferencesStore(dataDirectory: directory.Root);
        Assert.Equal(5, restarted.Current.RefreshMinutes); Assert.True(restarted.Current.AdaptiveRefresh); Assert.Equal(72, restarted.Current.ExpiryLeadHours);
        Assert.Equal("Travail", PrivacyText.Account(Profile, State(null), restarted.Current));
        Assert.False(restarted.Current.PrivacyMode);
        Assert.Equal("private@example.test", Profile.Email);
        var other = new AccountProfile(Guid.NewGuid(), "other@example.test");
        Assert.Equal(other.Email, PrivacyText.Account(other, State(null), restarted.Current));
    }

    [Fact]
    public void LegacyAndInvalidPreferencesUseSafeDefaults()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("preferences.json"), "{\"refreshMinutes\":0,\"expiryLeadHours\":-1,\"appearances\":null,\"sentExpiryReminders\":null}");
        var store = new PreferencesStore(dataDirectory: directory.Root);
        Assert.Equal(2, store.Current.RefreshMinutes); Assert.Equal(24, store.Current.ExpiryLeadHours);
        Assert.Empty(store.Current.Appearances); Assert.Empty(store.Current.SentExpiryReminders);
    }

    [Theory]
    [InlineData(1, false, 3600, 1)]
    [InlineData(2, true, 299, 2)]
    [InlineData(2, true, 300, 10)]
    [InlineData(5, true, 301, 10)]
    [InlineData(5, true, 0, 5)]
    [InlineData(-1, false, 0, 2)]
    public void AdaptiveRefreshReturnsToConfiguredRate(int minutes, bool adaptive, int idleSeconds, int expected)
        => Assert.Equal(TimeSpan.FromMinutes(expected), RefreshPolicy.Interval(minutes, adaptive, TimeSpan.FromSeconds(idleSeconds)));

    [Fact]
    public void ExpiryAlertsUseKnownAvailabilityAndDeduplicateAcrossRestarts()
    {
        var state = State(Snapshot()); var due = Assert.Single(ExpiryReminders.Due(state, Now, 24, new Dictionary<string, DateTimeOffset>()));
        Assert.Equal(Profile.Id, due.AccountId); Assert.Equal(Now.AddHours(-4), due.ObservedAt);
        using var directory = new TestDirectory(); var store = new PreferencesStore(dataDirectory: directory.Root);
        store.Update(p => p with { SentExpiryReminders = new() { [due.Key] = due.ExpiresAt } });
        var restarted = new PreferencesStore(dataDirectory: directory.Root);
        Assert.Empty(ExpiryReminders.Due(state, Now, 24, restarted.Current.SentExpiryReminders));
        Assert.Empty(ExpiryReminders.Due(State(Snapshot(0)), Now, 24, new Dictionary<string, DateTimeOffset>()));
        Assert.Empty(ExpiryReminders.Due(State(Snapshot(null)), Now, 24, new Dictionary<string, DateTimeOffset>()));
        Assert.Empty(ExpiryReminders.Due(state, Now.AddDays(1), 24, new Dictionary<string, DateTimeOffset>()));
    }

    [Fact]
    public void ExpiryLeadTimeAndAccountsRemainIndependent()
    {
        var snapshot = Snapshot() with { ResetCredits = [new("shared", null, null, Now.AddHours(48))] };
        var state = State(snapshot);
        Assert.Empty(ExpiryReminders.Due(state, Now, 24, new Dictionary<string, DateTimeOffset>()));
        var reminder = Assert.Single(ExpiryReminders.Due(state, Now, 72, new Dictionary<string, DateTimeOffset>()));
        var other = new AccountProfile(Guid.NewGuid(), "second@example.test");
        state = state with { Accounts = [..state.Accounts, new(other, snapshot with { Email = other.Email })] };
        var remaining = Assert.Single(ExpiryReminders.Due(state, Now, 72, new Dictionary<string, DateTimeOffset> { [reminder.Key] = reminder.ExpiresAt }));
        Assert.Equal(other.Id, remaining.AccountId);
    }

    [Theory]
    [InlineData("2026-10-25T02:30:00+02:00", "20261025T003000Z/20261025T003500Z")]
    [InlineData("2026-10-25T02:30:00+01:00", "20261025T013000Z/20261025T013500Z")]
    public void GoogleDraftPreservesDstInstants(string at, string dates)
    {
        var uri = CalendarExport.GoogleEventLink(new("id", "Reset", "", DateTimeOffset.Parse(at)));
        Assert.Equal("https", uri.Scheme); Assert.Equal("calendar.google.com", uri.Host);
        Assert.Contains("dates=" + dates, Uri.UnescapeDataString(uri.Query));
    }

    [Fact]
    public void GoogleDraftEncodesTextWithoutInjectingParameters()
    {
        var title = "Été 😀 &add=other@example.test#fragment";
        var description = "First\nSecond &dates=wrong";
        var uri = CalendarExport.GoogleEventLink(new("id", title, description, Now));
        var fields = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert.Equal(6, fields.Count); Assert.Equal(title, fields["text"]); Assert.Equal(description, fields["details"]);
        Assert.Empty(uri.Fragment); Assert.False(fields.ContainsKey("add"));
    }

    [Fact]
    public void CalendarOnlyExportsFutureKnownDatesWithStablePrivateIdentifiers()
    {
        var state = State(Snapshot());
        var entries = CalendarExport.Entries(state, p => PrivacyText.Account(p, state, true), Now);
        Assert.Equal(3, entries.Count); Assert.All(entries, e => Assert.Contains("Compte 01", e.Title));
        var ics = CalendarExport.Serialize(entries, Now);
        Assert.DoesNotContain(Profile.Email, ics); Assert.DoesNotContain("Private title", ics);
        Assert.Equal(entries.Select(e => e.Uid), CalendarExport.Entries(state, _ => "Changed alias", Now).Select(e => e.Uid));
        Assert.Empty(CalendarExport.Entries(state, _ => "Test", Now.AddDays(3)));
        Assert.Empty(CalendarExport.Entries(State(null), _ => "Test", Now));
        Assert.Equal(2, CalendarExport.Entries(State(Snapshot(0)), _ => "Test", Now).Count);
    }

    [Fact]
    public void CalendarEscapesInjectionFoldsUtf8AndPreservesDstInstants()
    {
        var title = "Été 😀,;\\\r\nBEGIN:VEVENT " + new string('é', 100);
        var first = DateTimeOffset.Parse("2026-10-25T02:30:00+02:00");
        var second = DateTimeOffset.Parse("2026-10-25T02:30:00+01:00");
        var ics = CalendarExport.Serialize([new("one", title, "Description", first), new("two", "Second", "", second)], Now);
        Assert.Contains("DTSTART:20261025T003000Z\r\n", ics); Assert.Contains("DTSTART:20261025T013000Z\r\n", ics);
        Assert.All(ics.Split("\r\n"), line => Assert.InRange(Encoding.UTF8.GetByteCount(line), 0, 75));
        var unfolded = ics.Replace("\r\n ", "");
        Assert.Equal(2, unfolded.Split("\r\n").Count(line => line == "BEGIN:VEVENT"));
        Assert.Contains("SUMMARY:Été 😀\\,\\;\\\\\\nBEGIN:VEVENT ", unfolded);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }
}
