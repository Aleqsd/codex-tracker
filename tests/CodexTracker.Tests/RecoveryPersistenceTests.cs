using System.Text.Json;
using CodexTracker.App;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class RecoveryPersistenceTests
{
    [Fact]
    public void FreshProfileKeepsDefaultsAndDoesNotNeedRecovery()
    {
        using var directory = new TestDirectory();
        var store = new PreferencesStore(dataDirectory: directory.Root);
        Assert.False(store.RecoveryRequired);
        Assert.Null(store.RecoveryWarning);
        Assert.False(store.Current.IncludePrereleaseUpdates);
        Assert.Contains(store.Current.ReminderRules!, rule => rule.Enabled);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public void AtomicBackupContainsThePreviousValidPreferences()
    {
        using var directory = new TestDirectory();
        var store = new PreferencesStore(dataDirectory: directory.Root);
        store.Update(p => p with { ThemeMode = ThemeMode.Light });
        var first = File.ReadAllBytes(directory.File("preferences.json"));
        Assert.Equal(first, File.ReadAllBytes(directory.File("preferences.json.bak")));
        store.Update(p => p with { ThemeMode = ThemeMode.Dark });
        Assert.Equal(first, File.ReadAllBytes(directory.File("preferences.json.bak")));
        Assert.Equal(ThemeMode.Dark, new PreferencesStore(dataDirectory: directory.Root).Current.ThemeMode);
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void RecoveryPreservesDataButNeverRestoresPreviousSendAuthorizations()
    {
        using var directory = new TestDirectory();
        var store = new PreferencesStore(dataDirectory: directory.Root);
        var sent = new Dictionary<string, DateTimeOffset> { ["fictional-reserve"] = DateTimeOffset.Parse("2026-09-20T12:00:00Z") };
        store.Update(p => p with { ThemeMode = ThemeMode.Light, McpEnabled = true, SentExpiryReminders = sent,
            ReminderRules = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Sms, ReminderChannel.Email])] });
        var backup = File.ReadAllBytes(directory.File("preferences.json.bak"));
        File.WriteAllText(directory.File("preferences.json"), "{broken");
        File.WriteAllText(directory.File("reminder-journal.json"), "existing journal must stay byte-identical");
        File.WriteAllText(directory.File("notification-secrets.dpapi"), "existing protected secret bytes");
        var recovered = new PreferencesStore(dataDirectory: directory.Root);
        Assert.Equal(ThemeMode.Light, recovered.Current.ThemeMode);
        Assert.True(recovered.RecoveryRequired);
        Assert.False(recovered.Current.McpEnabled);
        Assert.False(recovered.Current.Alert20 || recovered.Current.Alert10 || recovered.Current.Alert5 || recovered.Current.ResetNotifications || recovered.Current.ExpiryNotifications);
        Assert.All(recovered.Current.ReminderRules!, rule => Assert.False(rule.Enabled));
        Assert.Equal(sent, recovered.Current.SentExpiryReminders);
        Assert.Equal("{broken", File.ReadAllText(directory.File("preferences.json")));
        Assert.Equal(backup, File.ReadAllBytes(directory.File("preferences.json.bak")));
        Assert.DoesNotContain(directory.Root, recovered.RecoveryWarning!);

        recovered.Update(p => p with { SortMode = SortMode.Reset });
        Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "preferences.json.corrupt-*"))));
        Assert.Equal(backup, File.ReadAllBytes(directory.File("preferences.json.bak")));
        var restarted = new PreferencesStore(dataDirectory: directory.Root);
        Assert.True(restarted.RecoveryRequired);
        Assert.All(restarted.Current.ReminderRules!, rule => Assert.False(rule.Enabled));
        restarted.AcknowledgeRecovery();
        Assert.False(new PreferencesStore(dataDirectory: directory.Root).RecoveryRequired);
        Assert.All(restarted.Current.ReminderRules!, rule => Assert.False(rule.Enabled));
        Assert.Equal("existing journal must stay byte-identical", File.ReadAllText(directory.File("reminder-journal.json")));
        Assert.Equal("existing protected secret bytes", File.ReadAllText(directory.File("notification-secrets.dpapi")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    public void NoBackupRequiresAcknowledgementAndKeepsNotificationsOff(string malformed)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("preferences.json"), malformed);
        var store = new PreferencesStore(dataDirectory: directory.Root);
        Assert.True(store.RecoveryRequired);
        Assert.All(store.Current.ReminderRules!, rule => Assert.False(rule.Enabled));
        Assert.False(store.Current.McpEnabled);
        store.AcknowledgeRecovery();
        Assert.False(store.RecoveryRequired);
        Assert.Equal(malformed, File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "preferences.json.corrupt-*"))));
        Assert.All(new PreferencesStore(dataDirectory: directory.Root).Current.ReminderRules!, rule => Assert.False(rule.Enabled));
    }

    [Fact]
    public void MissingPrimaryUsesBackupWithoutReenablingMcp()
    {
        using var directory = new TestDirectory();
        var store = new PreferencesStore(dataDirectory: directory.Root);
        store.Update(p => p with { McpEnabled = true });
        File.Delete(directory.File("preferences.json"));
        var recovered = new PreferencesStore(dataDirectory: directory.Root);
        Assert.True(recovered.RecoveryRequired);
        Assert.False(recovered.Current.McpEnabled);
    }

    [Fact]
    public void InvalidBackupCannotBeLoadedAsDefaultsAndIsPreservedBeforeWrite()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("preferences.json"), "broken primary");
        File.WriteAllText(directory.File("preferences.json.bak"), "null");
        var store = new PreferencesStore(dataDirectory: directory.Root);
        Assert.True(store.RecoveryRequired);
        store.AcknowledgeRecovery();
        Assert.Equal("null", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "preferences.json.bak.corrupt-*"))));
        Assert.Equal("broken primary", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "preferences.json.corrupt-*"))));
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void LockedPrimaryNeverChangesMemoryOrDestroysTheBackup()
    {
        using var directory = new TestDirectory();
        var store = new PreferencesStore(dataDirectory: directory.Root);
        store.Update(p => p with { ThemeMode = ThemeMode.Light });
        var previousRevision = store.Revision;
        var backup = File.ReadAllBytes(directory.File("preferences.json.bak"));
        using (var locked = new FileStream(directory.File("preferences.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.Update(p => p with { ThemeMode = ThemeMode.Dark }));
            Assert.Equal(ThemeMode.Light, store.Current.ThemeMode);
            Assert.Equal(previousRevision, store.Revision);
            Assert.Equal(backup, File.ReadAllBytes(directory.File("preferences.json.bak")));
        }
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
        Assert.Equal(ThemeMode.Light, new PreferencesStore(dataDirectory: directory.Root).Current.ThemeMode);
    }

    [Fact]
    public void DamagedBackupIsPreservedEvenWhenThePrimaryIsStillValid()
    {
        using var directory = new TestDirectory();
        var store = new PreferencesStore(dataDirectory: directory.Root);
        store.Update(p => p with { ThemeMode = ThemeMode.Light });
        File.WriteAllText(directory.File("preferences.json.bak"), "broken backup");
        store.Update(p => p with { ThemeMode = ThemeMode.Dark });
        Assert.Equal("broken backup", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "preferences.json.bak.corrupt-*"))));
        Assert.Equal(ThemeMode.Dark, new PreferencesStore(dataDirectory: directory.Root).Current.ThemeMode);
    }

    [Fact]
    public void AccountsRecoverFromValidatedBackupAndKeepCorruptedOriginal()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var profile = new AccountProfile(Guid.NewGuid(), "fictional@example.test");
        var settings = new StoredSettings([profile], profile.Id, true, [profile.Id]);
        store.SaveSettings(settings);
        File.WriteAllText(directory.File("settings.json"), "{broken");
        Assert.Equal(profile, Assert.Single(store.LoadSettings().Accounts));
        Assert.Single(store.RecoveryWarnings);
        Assert.DoesNotContain(profile.Email, store.RecoveryWarnings[0]);
        store.SaveSettings(settings);
        Assert.Equal("{broken", File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root, "settings.json.corrupt-*"))));
        Assert.Equal(profile, Assert.Single(store.LoadSettings().Accounts));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"accounts\":null}")]
    [InlineData("{broken")]
    public void UnrecoverableAccountsNeverFallBackToEmptyOrSeedAccounts(string malformed)
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        File.WriteAllText(directory.File("settings.json"), malformed);
        File.WriteAllText(directory.File("initial-accounts.json"), "[\"seed@example.test\"]");
        var error = Assert.Throws<TrackerException>(store.LoadSettings);
        Assert.DoesNotContain(directory.Root, error.Message);
        Assert.Equal(malformed, File.ReadAllText(directory.File("settings.json")));
        Assert.False(File.Exists(directory.File("settings.json.bak")));
    }

    [Fact]
    public void InvalidAccountWriteCannotReplaceAValidPrimaryOrBackup()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var profile = new AccountProfile(Guid.NewGuid(), "fictional@example.test");
        store.SaveSettings(new([profile], null, true));
        var bytes = File.ReadAllBytes(directory.File("settings.json"));
        Assert.Throws<JsonException>(() => store.SaveSettings(new([profile, profile], null, true)));
        Assert.Equal(bytes, File.ReadAllBytes(directory.File("settings.json")));
        Assert.Equal(bytes, File.ReadAllBytes(directory.File("settings.json.bak")));
    }

    [Fact]
    public void RecoveredSnapshotsRetainTheirObservationDatesAndUnknownValues()
    {
        using var directory = new TestDirectory();
        using var store = new ProfileStore(directory.Root);
        store.Open();
        var id = Guid.NewGuid();
        var observed = DateTimeOffset.Parse("2026-09-20T12:00:00Z");
        var snapshot = new AccountSnapshot("fictional@example.test", null, [], null, null, observed);
        store.SaveSnapshots(new() { [id] = snapshot });
        File.WriteAllText(directory.File("snapshots.json"), "null");
        var restored = store.LoadSnapshots()[id];
        Assert.Equal(observed, restored.FetchedAt);
        Assert.Null(restored.AvailableResetCredits);
        Assert.Null(restored.Weekly);
        Assert.Single(store.RecoveryWarnings);
        Assert.Equal("null", File.ReadAllText(directory.File("snapshots.json")));
    }
}
