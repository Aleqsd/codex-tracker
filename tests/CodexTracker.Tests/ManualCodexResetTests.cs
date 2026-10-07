using CodexTracker.App;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ManualCodexResetTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T12:00:00Z");
    private static AccountState Account(AccountProvider provider = AccountProvider.Codex, bool active = false) => new(
        new(Guid.NewGuid(), "demo@example.test", provider),
        new("demo@example.test", "pro", [new(provider == AccountProvider.Codex ? "codex" : "claude", "Quota", [new(80, 10080, Now.AddDays(2)), new(50, 300, Now.AddHours(2))])],
            2, [new("credit", "Reserve", Now.AddDays(-2), Now.AddDays(1))], Now.AddHours(-1)),
        IsActiveInCodex: provider == AccountProvider.Codex && active, IsActiveInClaudeCode: provider == AccountProvider.ClaudeCode && active);

    [Fact]
    public void DeclarationCoversAllCodexAccountsIncludingActiveAndUnknownButKeepsMeasuredDataAndClaude()
    {
        var active = Account(active: true); var inactive = Account(); var unknown = Account() with { Snapshot = null }; var claude = Account(AccountProvider.ClaudeCode, true);
        var snapshots = new[] { active.Snapshot, inactive.Snapshot, unknown.Snapshot, claude.Snapshot };
        var state = new TrackerState([active, inactive, unknown, claude], active.Profile.Id) { ManualCodexReset = new(Now.AddMinutes(-30), Now) };
        foreach (var account in state.Accounts.Take(3))
        {
            Assert.Equal(100, QuotaPresentation.Remaining(state, account, ResetKind.Weekly, Now));
            Assert.Equal(100, QuotaPresentation.Remaining(state, account, ResetKind.Short, Now));
            Assert.Null(QuotaPresentation.ResetsAt(state, account, ResetKind.Weekly, Now));
        }
        Assert.Equal(20, QuotaPresentation.Remaining(state, claude, ResetKind.Weekly, Now));
        Assert.Equal(snapshots, state.Accounts.Select(a => a.Snapshot)); Assert.Equal(2, active.Snapshot!.AvailableResetCredits);
        Assert.Empty(ExpectedReset.Due(state, Now));
        var clear = state with { ManualCodexReset = null };
        Assert.Equal(20, QuotaPresentation.Remaining(clear, active, ResetKind.Weekly, Now));
    }

    [Fact]
    public void NewMeasurementWinsAndNoOldQuotaDeadlinesAreScheduledOrExported()
    {
        var old = Account(); var newer = Account() with { Snapshot = Account().Snapshot! with { FetchedAt = Now.AddMinutes(-5) } };
        var state = new TrackerState([old, newer], null) { ManualCodexReset = new(Now.AddMinutes(-30), Now) };
        Assert.Equal(20, QuotaPresentation.Remaining(state, newer, ResetKind.Weekly, Now));
        var schedule = ResetSchedule.Entries(state, old.Profile.Id, Now);
        Assert.All(schedule.Where(e => e.Kind != ResetKind.Reserve), e => Assert.Null(e.At));
        Assert.NotNull(schedule.Single(e => e.Kind == ResetKind.Reserve && e.CreditId == "credit").At);
        var export = CalendarExport.Entries(state, a => a.Email, Now);
        Assert.Equal(1, export.Count(e => e.Uid.StartsWith(CalendarExport.Identity(old.Profile.Id, "credit", "credit", Now.AddDays(1)))));
        var rules = new[] { new ReminderRule(ResetKind.Weekly, true, [10080], [ReminderChannel.Windows]) };
        Assert.DoesNotContain(ReminderPlanner.Due(state, rules, Now), r => r.AccountId == old.Profile.Id);
    }

    [Fact]
    public void DeclarationExpiresPerWindowAndRefusesFutureOrInconsistentDates()
    {
        var account = Account() with { Snapshot = null }; var reset = new ManualCodexReset(Now, Now);
        Assert.True(reset.Applies(account, ResetKind.Short, Now.AddHours(5).AddTicks(-1)));
        Assert.False(reset.Applies(account, ResetKind.Short, Now.AddHours(5)));
        Assert.True(reset.Applies(account, ResetKind.Weekly, Now.AddDays(7).AddTicks(-1)));
        Assert.False(reset.Applies(account, ResetKind.Weekly, Now.AddDays(7)));
        Assert.False(reset.Applies(account, ResetKind.Reserve, Now));
        Assert.False(new ManualCodexReset(Now.AddMinutes(1), Now).Applies(account, ResetKind.Short, Now));
        Assert.False(reset.Applies(Account(AccountProvider.ClaudeCode), ResetKind.Weekly, Now));
    }

    [Fact]
    public void CommandPersistsAndIsRepeatableAndRejectsFutureAndStaleFormWithoutOverwritingAnything()
    {
        using var directory = new TestDirectory(); var preferences = new PreferencesStore(dataDirectory: directory.Root);
        var commands = new ApplicationCommands(preferences, new NotificationSecretStore(directory.Root)); var originalRevision = commands.Revision;
        Assert.Throws<ArgumentException>(() => commands.DeclareCodexReset(Now.AddMinutes(1), Now, originalRevision));
        Assert.Equal(originalRevision, commands.Revision);
        commands.DeclareCodexReset(Now.AddMinutes(-30), Now, originalRevision); var revision = commands.Revision;
        var file = File.ReadAllBytes(directory.File("preferences.json"));
        commands.DeclareCodexReset(Now.AddMinutes(-30), Now.AddMinutes(1), revision);
        Assert.Equal(revision, commands.Revision); Assert.Equal(file, File.ReadAllBytes(directory.File("preferences.json")));
        Assert.Throws<InvalidOperationException>(() => commands.DeclareCodexReset(null, Now, originalRevision));
        var restarted = new PreferencesStore(dataDirectory: directory.Root); Assert.Equal(preferences.Current.ManualCodexReset, restarted.Current.ManualCodexReset);
        commands.DeclareCodexReset(null, Now, revision); Assert.Null(preferences.Current.ManualCodexReset);
        Assert.Null(new PreferencesStore(dataDirectory: directory.Root).Current.ManualCodexReset);
    }
}
