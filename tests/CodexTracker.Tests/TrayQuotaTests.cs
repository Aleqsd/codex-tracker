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
        // Claude Code closed: the remaining active account keeps the chosen window when its plan has one.
        Assert.Equal((plus, Short), Resolve(new([plus], null), AccountProvider.ClaudeCode, Short));
        Assert.Equal((pro, Weekly), Resolve(new([pro], null), AccountProvider.ClaudeCode, Short));
        // Codex Pro has no 5-hour quota: its week stays in the icon.
        Assert.Equal((pro, Weekly), Resolve(new([pro, claude], null), AccountProvider.Codex, Short));
        // An inactive account is never drawn, whatever was chosen.
        var closed = claude with { IsActiveInClaudeCode = false };
        Assert.Equal(((AccountState?)null, Short), Resolve(new([closed], null), AccountProvider.ClaudeCode, Short));
        Assert.Equal((plus, Weekly), Resolve(new([closed, plus], null), AccountProvider.ClaudeCode, Weekly));
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
