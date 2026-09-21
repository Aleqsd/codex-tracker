using CodexTracker.App;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class PreferencesTests
{
    [Fact]
    public void PreferencesSurviveRestartAndDemoDoesNotOverwriteThem()
    {
        using var directory = new TestDirectory();
        var preferences = new PreferencesStore(dataDirectory: directory.Root);
        preferences.Update(p => p with { ThemeMode = ThemeMode.Light, SortMode = SortMode.Reset, Alert10 = false });
        var persisted = File.ReadAllText(directory.File("preferences.json"));
        var demo = new PreferencesStore(false, directory.Root);
        demo.Update(p => p with { ThemeMode = ThemeMode.Dark });
        Assert.Equal(persisted, File.ReadAllText(directory.File("preferences.json")));
        var restarted = new PreferencesStore(dataDirectory: directory.Root);
        Assert.False(restarted.Current.PrivacyMode);
        Assert.Equal(ThemeMode.Light, restarted.Current.ThemeMode);
        Assert.Equal(SortMode.Reset, restarted.Current.SortMode);
        Assert.False(restarted.Current.Alert10);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    public void CorruptedPreferencesKeepDefaultsWithoutRewritingTheFile(string content)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("preferences.json"), content);
        var preferences = new PreferencesStore(dataDirectory: directory.Root);
        Assert.False(preferences.Current.PrivacyMode);
        Assert.Equal(content, File.ReadAllText(directory.File("preferences.json")));
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(10, false)]
    [InlineData(5, true)]
    [InlineData(1, false)]
    public void NotificationThresholdPreferencesAreIndependent(int threshold, bool enabled)
    {
        var notification = new QuotaNotification(Guid.NewGuid(), "private@example.test", NotificationKind.Threshold, threshold, DateTimeOffset.UtcNow);
        Assert.Equal(enabled, NotificationPolicy.IsEnabled(notification, new() { Alert10 = false }));
    }

    [Fact]
    public void SystemNotificationUsesTheAccountDisplayName()
    {
        var profile = new AccountProfile(Guid.NewGuid(), "private@example.test");
        var state = new TrackerState([new(profile)], profile.Id);
        var threshold = new QuotaNotification(profile.Id, profile.Email, NotificationKind.Threshold, 10, DateTimeOffset.UtcNow);
        var (title, body) = NotificationPolicy.Compose(threshold, state);
        Assert.Contains(profile.Email, title + body);
        Assert.Contains(profile.Email, body);
        var reset = threshold with { Kind = NotificationKind.Reset, Threshold = null, Window = UsageWindowKind.Short };
        var resetMessage = NotificationPolicy.Compose(reset, state);
        Assert.Contains(profile.Email, resetMessage.Title + resetMessage.Body);
        Assert.Contains("5 heures", resetMessage.Body);
        Assert.False(NotificationPolicy.IsEnabled(reset, new() { ResetNotifications = false }));
    }

    [Fact]
    public void PrivacyLabelsAreDistinctAndNeverUseInitialsOrEmailFragments()
    {
        var first = new AccountProfile(Guid.NewGuid(), "first@example.test");
        var second = new AccountProfile(Guid.NewGuid(), "second@example.test");
        var state = new TrackerState([new(first), new(second)], first.Id);
        Assert.Equal("Compte 01", PrivacyText.Account(first, state, true));
        Assert.Equal("Compte 02", PrivacyText.Account(second, state, true));
        Assert.Equal(second.Email, PrivacyText.Account(second, state, false));
        Assert.Equal("Compte masqué", PrivacyText.Email(first.Email, true));
    }
}
