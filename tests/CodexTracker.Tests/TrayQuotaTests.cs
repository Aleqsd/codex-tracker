using CodexTracker.App;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;
using static CodexTracker.Core.ResetKind;

namespace CodexTracker.Tests;

public sealed class TrayQuotaTests
{
    [Fact]
    public void ChosenToolAndWindowAreShownWhileItsAccountIsActive()
    {
        var codex = Active(AccountProvider.Codex, "plus");
        var claude = Active(AccountProvider.ClaudeCode);
        var state = new TrackerState([claude, codex], null);
        // The default keeps the former icon: the Codex week first.
        Assert.Equal((codex, Weekly), TrayQuota.Resolve(state, new()));
        Assert.Equal((claude, Short), Resolve(state, AccountProvider.ClaudeCode, Short));
        Assert.Equal((claude, Weekly), Resolve(state, AccountProvider.ClaudeCode, Weekly));
        Assert.Equal((codex, Short), Resolve(state, AccountProvider.Codex, Short));
    }

    [Fact]
    public void MissingToolOrQuotaFallsBackWithoutEmptyingTheIcon()
    {
        var pro = Active(AccountProvider.Codex, "pro");
        var plus = Active(AccountProvider.Codex, "plus");
        var claude = Active(AccountProvider.ClaudeCode);
        // Claude Code closed: the remaining tool shows the window last chosen for it, its week by default.
        Assert.Equal((plus, Weekly), Resolve(new([plus], null), AccountProvider.ClaudeCode, Short));
        var remembered = new TrackerPreferences { TrayProvider = AccountProvider.ClaudeCode, TrayWindow = Short, TrayWindows = new() { [AccountProvider.Codex] = Short } };
        Assert.Equal((plus, Short), TrayQuota.Resolve(new([plus], null), remembered));
        Assert.Equal((pro, Weekly), TrayQuota.Resolve(new([pro], null), remembered));
        // Codex Pro has no 5-hour quota: its week stays in the icon.
        Assert.Equal((pro, Weekly), Resolve(new([pro, claude], null), AccountProvider.Codex, Short));
        // An inactive account is never drawn, whatever was chosen.
        var closed = claude with { IsActiveInClaudeCode = false };
        Assert.Equal(((AccountState?)null, Short), Resolve(new([closed], null), AccountProvider.ClaudeCode, Short));
        Assert.Equal((plus, Weekly), Resolve(new([closed, plus], null), AccountProvider.ClaudeCode, Weekly));
    }

    [Fact]
    public void EachToolGetsItsOwnIconAndWindowOnlyWhenAskedAndBothAreInUse()
    {
        var codex = Active(AccountProvider.Codex, "plus");
        var pro = Active(AccountProvider.Codex, "pro");
        var claude = Active(AccountProvider.ClaudeCode);
        var both = new TrackerState([claude, codex], null);
        var chosen = new TrackerPreferences { TrayProvider = AccountProvider.ClaudeCode, TrayWindow = Short, TrayWindows = new() { [AccountProvider.ClaudeCode] = Short } };
        Assert.Equal([(claude, Short)], TrayQuota.Icons(both, chosen));
        var perTool = chosen with { TrayIconPerTool = true };
        // Codex first, as in the hover preview; Codex keeps its own default week.
        Assert.Equal([(codex, Weekly), (claude, Short)], TrayQuota.Icons(both, perTool));
        Assert.Equal([(codex, Short), (claude, Short)], TrayQuota.Icons(both, perTool with { TrayWindows = new() { [AccountProvider.Codex] = Short, [AccountProvider.ClaudeCode] = Short } }));
        Assert.Equal([(pro, Weekly), (claude, Short)], TrayQuota.Icons(new([pro, claude], null), perTool with { TrayWindows = new() { [AccountProvider.Codex] = Short, [AccountProvider.ClaudeCode] = Short } }));
        // A single tool in use, or none, keeps one icon.
        Assert.Equal([(claude, Short)], TrayQuota.Icons(new([claude, codex with { IsActiveInCodex = false }], null), perTool));
        Assert.Equal([((AccountState?)null, Short)], TrayQuota.Icons(new([], null), perTool));
        // Files from 0.9.26 only know the chosen tool's window.
        var legacy = new TrackerPreferences { TrayProvider = AccountProvider.ClaudeCode, TrayWindow = Short, TrayIconPerTool = true };
        Assert.Equal(Short, TrayQuota.WindowFor(legacy, AccountProvider.ClaudeCode));
        Assert.Equal(Weekly, TrayQuota.WindowFor(legacy, AccountProvider.Codex));
    }

    [Fact]
    public void EveryChoiceIsRememberedPerToolAndRepeatsWriteNothing()
    {
        using var directory = new TestDirectory();
        var preferences = new PreferencesStore(dataDirectory: directory.Root);
        var commands = new ApplicationCommands(preferences, new NotificationSecretStore(directory.Root));
        commands.SetTrayQuota(AccountProvider.Codex, Short);
        commands.SetTrayQuota(AccountProvider.ClaudeCode, Weekly);
        var current = preferences.Current;
        Assert.Equal((AccountProvider.ClaudeCode, Weekly), (current.TrayProvider, current.TrayWindow));
        Assert.Equal(Short, TrayQuota.WindowFor(current, AccountProvider.Codex));
        Assert.Equal(Weekly, TrayQuota.WindowFor(current, AccountProvider.ClaudeCode));
        var revision = commands.Revision;
        commands.SetTrayQuota(AccountProvider.ClaudeCode, Weekly);
        Assert.Equal(revision, commands.Revision);
        // From a 0.9.26 file, the first click records the tool's window even if the single choice is unchanged.
        File.WriteAllText(directory.File("preferences.json"), "{\"trayProvider\":\"ClaudeCode\",\"trayWindow\":\"Short\"}");
        var older = new PreferencesStore(dataDirectory: directory.Root);
        var olderCommands = new ApplicationCommands(older, new NotificationSecretStore(directory.Root));
        olderCommands.SetTrayQuota(AccountProvider.ClaudeCode, Short);
        Assert.Equal(Short, new PreferencesStore(dataDirectory: directory.Root).Current.TrayWindows[AccountProvider.ClaudeCode]);
        commands.SavePreferences(p => p with { TrayIconPerTool = true });
        Assert.True(new PreferencesStore(dataDirectory: directory.Root).Current.TrayIconPerTool);
        Assert.Throws<ArgumentException>(() => commands.SavePreferences(p => p with { TrayWindows = new() { [AccountProvider.Codex] = Reserve } }));
        Assert.Throws<ArgumentException>(() => commands.SavePreferences(p => p with { TrayWindows = new() { [(AccountProvider)7] = Weekly } }));
        var repaired = PreferencesStore.Sanitize(new TrackerPreferences { TrayWindows = new() { [(AccountProvider)7] = Weekly, [AccountProvider.Codex] = Reserve, [AccountProvider.ClaudeCode] = Short } });
        Assert.Equal(new Dictionary<AccountProvider, ResetKind> { [AccountProvider.ClaudeCode] = Short }, repaired.TrayWindows);
        Assert.Empty(PreferencesStore.Sanitize(new TrackerPreferences { TrayWindows = null! }).TrayWindows);
    }

    [Fact]
    public void ChoiceSurvivesRestartWhileInvalidValuesAreRefusedOrRepaired()
    {
        using var directory = new TestDirectory();
        var preferences = new PreferencesStore(dataDirectory: directory.Root);
        Assert.Equal((AccountProvider.Codex, Weekly), (preferences.Current.TrayProvider, preferences.Current.TrayWindow));
        var commands = new ApplicationCommands(preferences, new NotificationSecretStore(directory.Root));
        commands.SetTrayQuota(AccountProvider.ClaudeCode, Short);
        var revision = commands.Revision;
        commands.SetTrayQuota(AccountProvider.ClaudeCode, Short);
        Assert.Equal(revision, commands.Revision); // The same click twice writes nothing.
        var restarted = new PreferencesStore(dataDirectory: directory.Root);
        Assert.Equal((AccountProvider.ClaudeCode, Short), (restarted.Current.TrayProvider, restarted.Current.TrayWindow));

        Assert.Throws<ArgumentException>(() => commands.SetTrayQuota(AccountProvider.Codex, Reserve));
        Assert.Throws<ArgumentException>(() => commands.SetTrayQuota((AccountProvider)7, Weekly));
        Assert.Equal(revision, commands.Revision);
        Assert.Equal((AccountProvider.ClaudeCode, Short), (preferences.Current.TrayProvider, preferences.Current.TrayWindow));

        var imported = PreferencesStore.Sanitize(new TrackerPreferences { TrayProvider = (AccountProvider)7, TrayWindow = Reserve });
        Assert.Equal((AccountProvider.Codex, Weekly), (imported.TrayProvider, imported.TrayWindow));
        File.WriteAllText(directory.File("preferences.json"), "{\"trayProvider\":9,\"trayWindow\":\"Reserve\",\"alert10\":false}");
        var edited = new PreferencesStore(dataDirectory: directory.Root);
        Assert.False(edited.RecoveryRequired);
        Assert.False(edited.Current.Alert10);
        Assert.Equal((AccountProvider.Codex, Weekly), (edited.Current.TrayProvider, edited.Current.TrayWindow));
    }

    private static (AccountState? Account, ResetKind Window) Resolve(TrackerState state, AccountProvider provider, ResetKind window) =>
        TrayQuota.Resolve(state, new() { TrayProvider = provider, TrayWindow = window });

    private static AccountState Active(AccountProvider provider, string? plan = null) =>
        new(new AccountProfile(Guid.NewGuid(), "tray@example.test", provider, ProviderPlanType: plan),
            IsActiveInCodex: provider == AccountProvider.Codex, IsActiveInClaudeCode: provider == AccountProvider.ClaudeCode);
}
