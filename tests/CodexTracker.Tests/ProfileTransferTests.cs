using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexTracker.App;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ProfileTransferTests
{
    private const string Password = "fictional profile password";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void ArchiveIsUnreadableWithoutItsPasswordAndDetectsAnyChange()
    {
        var content = Encoding.UTF8.GetBytes("{\"fictional\":\"studio@example.test\"}");
        var archive = ProfileArchive.Protect(content, Password);
        Assert.Equal(content, ProfileArchive.Unprotect(archive, Password));
        Assert.DoesNotContain("studio@example.test", Encoding.UTF8.GetString(archive));
        Assert.Contains("Mot de passe incorrect", Assert.Throws<TrackerException>(() => ProfileArchive.Unprotect(archive, "another fictional password")).Message);

        var tampered = JsonNode.Parse(archive)!;
        var data = Convert.FromBase64String((string)tampered["Data"]!); data[0] ^= 1;
        tampered["Data"] = Convert.ToBase64String(data);
        Assert.Throws<TrackerException>(() => ProfileArchive.Unprotect(Encoding.UTF8.GetBytes(tampered.ToJsonString()), Password));
        var weakened = JsonNode.Parse(archive)!; weakened["Iterations"] = 1;
        Assert.Throws<TrackerException>(() => ProfileArchive.Unprotect(Encoding.UTF8.GetBytes(weakened.ToJsonString()), Password));
        var future = JsonNode.Parse(archive)!; future["Version"] = 2;
        Assert.Contains("plus récente", Assert.Throws<TrackerException>(() => ProfileArchive.Unprotect(Encoding.UTF8.GetBytes(future.ToJsonString()), Password)).Message);

        Assert.Throws<TrackerException>(() => ProfileArchive.Protect(content, "short"));
        Assert.Throws<TrackerException>(() => ProfileArchive.Unprotect("{}"u8.ToArray(), Password));
        Assert.Throws<TrackerException>(() => ProfileArchive.Unprotect("not json"u8.ToArray(), Password));
    }

    [Fact]
    public void HistoriesUniteWithoutDuplicatesInvalidOrExpiredPoints()
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid();
        UsageSample Point(Guid account, double hours, double weekly) => new(account, Now.AddHours(-hours), weekly, null);
        var merged = UsageAnalytics.Merge([Point(id, 2, 50), Point(id, 1, 40)],
            [Point(other, 1, 99), Point(other, 3, 60), Point(other, 24 * 100, 70), Point(other, 0.5, 140), Point(other, -2, 30)], id, Now);
        Assert.Equal([Now.AddHours(-3), Now.AddHours(-2), Now.AddHours(-1)], merged.Select(s => s.Timestamp));
        Assert.Equal([60d, 50d, 40d], merged.Select(s => s.WeeklyRemaining!.Value));
        Assert.All(merged, s => Assert.Equal(id, s.AccountId));
    }

    [Fact]
    public async Task ProfileMovesToAnotherPcWithItsAccountsHistorySettingsAndAvatars()
    {
        using var source = new TestDirectory(); using var target = new TestDirectory();
        var codex = new AccountProfile(Guid.NewGuid(), "studio@example.test");
        var claude = new AccountProfile(Guid.NewGuid(), "alex@example.test", AccountProvider.ClaudeCode, "fictional-org", "Fictional team");
        var observed = Now.AddHours(-3).AddTicks(-Now.Ticks % TimeSpan.TicksPerSecond);
        var history = Enumerable.Range(1, 30).Select(i => new UsageSample(codex.Id, observed.AddMinutes(-2 * i), 40 + i * 0.5, observed.AddDays(2))).Reverse().ToArray();
        var data = new ProfileData([codex, claude],
            new Dictionary<Guid, AccountSnapshot> { [codex.Id] = Snapshot(codex.Email, observed), [claude.Id] = Snapshot(claude.Email, observed.AddMinutes(-30), "claude") },
            new Dictionary<Guid, IReadOnlyList<UsageSample>> { [codex.Id] = history });

        byte[] archive;
        await using (var first = Service(source))
        {
            await first.InitializeAsync();
            Assert.Equal(2, (await first.ImportProfilesAsync(data)).Added);
            var preferences = new PreferencesStore(dataDirectory: source.File("prefs"));
            var avatar = AvatarStore.Save(preferences.DataDirectory, BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8));
            preferences.Update(p => p with
            {
                ThemeMode = App.ThemeMode.Light, McpEnabled = true,
                Appearances = new() { [codex.Id] = new("Studio", avatar) },
                ReminderRules = [new(ResetKind.Reserve, true, [360], [ReminderChannel.Windows], [codex.Id])]
            });
            archive = ProfileTransfer.Export(first, preferences, Password, Now);
        }
        Assert.DoesNotContain("studio@example.test", Encoding.UTF8.GetString(archive));

        // The new PC already follows the same Codex account under another id, with an older reading.
        var local = codex with { Id = Guid.NewGuid() };
        await using (var second = Service(target))
        {
            await second.InitializeAsync();
            await second.ImportProfilesAsync(new([local], new Dictionary<Guid, AccountSnapshot> { [local.Id] = Snapshot(local.Email, observed.AddDays(-1)) },
                new Dictionary<Guid, IReadOnlyList<UsageSample>>()));
            var preferences = new PreferencesStore(dataDirectory: target.File("prefs"));
            preferences.Update(p => p with { McpEnabled = false });
            var commands = new ApplicationCommands(preferences, new NotificationSecretStore(target.File("prefs")));
            Assert.Throws<TrackerException>(() => ProfileTransfer.Read(archive, "another fictional password"));
            var bundle = ProfileTransfer.Read(archive, Password);
            var result = await ProfileTransfer.ImportAsync(bundle, second, commands, preferences);

            Assert.Equal((1, 1), (result.Added, result.Updated));
            Assert.Equal(local.Id, result.Accounts[codex.Id]);
            Assert.Equal(2, second.State.Accounts.Count);
            var studio = second.State.Accounts.Single(a => a.Profile.Id == local.Id);
            Assert.Equal(observed, studio.Snapshot!.FetchedAt);
            Assert.False(studio.IsActive);
            Assert.Equal(history.Length, second.GetHistory(local.Id).Count);
            Assert.All(second.GetHistory(local.Id), s => Assert.Equal(local.Id, s.AccountId));
            Assert.Equal("Fictional team", second.State.Accounts.Single(a => a.Profile.Provider == AccountProvider.ClaudeCode).Profile.OrganizationName);

            var current = preferences.Current;
            Assert.Equal(App.ThemeMode.Light, current.ThemeMode);
            Assert.False(current.McpEnabled);
            Assert.Equal("Studio", current.Appearances[local.Id].Name);
            Assert.True(File.Exists(AvatarStore.PathFor(preferences.DataDirectory, current.Appearances[local.Id].AvatarFile)));
            Assert.Equal([local.Id], current.ReminderRules!.Single(r => r.Kind == ResetKind.Reserve && r.AccountIds is not null).AccountIds!);

            // Importing the same file again changes nothing and duplicates nothing.
            var again = await ProfileTransfer.ImportAsync(ProfileTransfer.Read(archive, Password), second, commands, preferences);
            Assert.Equal((0, 0), (again.Added, again.Updated));
            Assert.Equal(history.Length, second.GetHistory(local.Id).Count);
        }
        await using var restarted = Service(target);
        await restarted.InitializeAsync();
        Assert.Equal(2, restarted.State.Accounts.Count);
        Assert.Equal(history.Length, restarted.GetHistory(local.Id).Count);
    }

    [Fact]
    public async Task UnusableOrForeignObservationsAreRefused()
    {
        using var directory = new TestDirectory();
        await using var service = Service(directory);
        await service.InitializeAsync();
        await Assert.ThrowsAsync<TrackerException>(() => service.ImportProfilesAsync(new([new(Guid.NewGuid(), " ")], new Dictionary<Guid, AccountSnapshot>(), new Dictionary<Guid, IReadOnlyList<UsageSample>>())));
        // Claude accounts need their organisation; a snapshot for another address or from the future is dropped.
        var codex = new AccountProfile(Guid.NewGuid(), "studio@example.test");
        var result = await service.ImportProfilesAsync(new([codex, new(Guid.NewGuid(), "alex@example.test", AccountProvider.ClaudeCode)],
            new Dictionary<Guid, AccountSnapshot> { [codex.Id] = Snapshot("other@example.test", Now.AddHours(-1)) }, new Dictionary<Guid, IReadOnlyList<UsageSample>>()));
        Assert.Equal(1, result.Added);
        Assert.Null(Assert.Single(service.State.Accounts).Snapshot);
        await service.ImportProfilesAsync(new([codex], new Dictionary<Guid, AccountSnapshot> { [codex.Id] = Snapshot(codex.Email, Now.AddHours(2)) }, new Dictionary<Guid, IReadOnlyList<UsageSample>>()));
        Assert.Null(Assert.Single(service.State.Accounts).Snapshot);
    }

    private static TrackerService Service(TestDirectory directory) => new(directory.File("missing-auth.json"), new TrackerServiceOptions
    {
        DataDirectory = directory.File("data"), ClaudeLocation = ClaudeFixture.Location(directory), ClaudeDesktopUsagePaths = [],
        AutomaticRefresh = false, MonitorAuthChanges = false
    });
    private static AccountSnapshot Snapshot(string email, DateTimeOffset at, string bucket = "codex") =>
        new(email, "plus", [new(bucket, null, [new(40, 10080, at.AddDays(2)), new(10, 300, at.AddHours(2))])], 1,
            [new("credit-A", "Réserve", at.AddDays(-1), at.AddDays(3))], at);
}
