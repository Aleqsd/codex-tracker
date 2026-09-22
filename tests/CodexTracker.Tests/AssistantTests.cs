using CodexTracker.App;
using CodexTracker.App.Updates;
using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class AssistantTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-09-21T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void ConfirmationExpiresAtExactlyFiveMinutes()
    {
        var clock = new Clock(); var actions = new AssistantActions(null, clock); var id = Guid.NewGuid().ToString();
        actions.Begin(id, "hash", true, "pending"); clock.Now = clock.Now.AddMinutes(5).AddTicks(-1);
        Assert.Equal("pending", actions.Find(id)!.Status); clock.Now = clock.Now.AddTicks(1);
        Assert.Equal("expired", actions.Find(id)!.Status);
    }
    [Fact]
    public void RepeatedIdReturnsReceiptAndDifferentPayloadIsRejected()
    {
        var actions = new AssistantActions(null); var id = Guid.NewGuid().ToString();
        actions.Begin(id, "a", false, ""); actions.Set(id, "completed", "done");
        Assert.Equal("completed", actions.Begin(id, "a", false, "").Status);
        Assert.Throws<InvalidOperationException>(() => actions.Begin(id, "b", false, ""));
    }
    [Theory] [InlineData("pending", "cancelled")] [InlineData("executing", "unknown")] [InlineData("completed", "completed")]
    public void RestartNeverReplaysAnAction(string before, string after)
    {
        var dir = Temp();
        try
        {
            var actions = new AssistantActions(dir); var id = Guid.NewGuid().ToString();
            actions.Begin(id, "fingerprint", true, "pending"); actions.Set(id, before, "state");
            Assert.Equal(after, new AssistantActions(dir).Find(id)!.Status);
            Assert.DoesNotContain("apiKey", File.ReadAllText(Path.Combine(dir, "assistant-actions.json")));
        }
        finally { Clean(dir); }
    }
    [Fact]
    public void CorruptActionJournalFailsClosed()
    {
        var dir = Temp(); try { File.WriteAllText(Path.Combine(dir, "assistant-actions.json"), "broken"); Assert.ThrowsAny<Exception>(() => new AssistantActions(dir)); } finally { Clean(dir); }
    }
    [Fact]
    public void UiAndMcpUseSameRevisionAndSecretValidation()
    {
        var dir = Temp();
        try
        {
            var p = new PreferencesStore(false, dir); var store = new NotificationSecretStore(dir); var commands = new ApplicationCommands(p, store);
            var before = commands.Revision; commands.SavePreferences(p => p with { ThemeMode = ThemeMode.Light }, before);
            Assert.Throws<InvalidOperationException>(() => commands.SavePreferences(p => p with { ThemeMode = ThemeMode.Dark }, before));
            before = commands.Revision; commands.SaveSecrets(NotificationSecrets.Empty with { SendGrid = new(ApiKey: "FICTIONAL-SECRET") }, before);
            Assert.NotEqual(before, commands.Revision); Assert.False(store.Read().SendGrid.Enabled);
            Assert.DoesNotContain("FICTIONAL-SECRET", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "notification-secrets.dpapi"))));
            Assert.Throws<ArgumentException>(() => commands.SaveSecrets(NotificationSecrets.Empty with { SendGrid = new(Enabled: true) }));
        }
        finally { Clean(dir); }
    }
    [Fact]
    public void LocalDisableAndWindowsRulesNeedNoExternalConfirmation()
    {
        ReminderRule[] old = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Sms])];
        Assert.False(ApplicationCommands.ExpandsExternalRules(old, []));
        Assert.False(ApplicationCommands.ExpandsExternalRules(old, [new(ResetKind.Weekly, true, [1440], [ReminderChannel.Windows])]));
        Assert.True(ApplicationCommands.ExpandsExternalRules(old, [new(ResetKind.Weekly, true, [1440], [ReminderChannel.Sms])]));
        Assert.True(ApplicationCommands.ExpandsExternalRules(old, [new(ResetKind.Weekly, true, [60], [ReminderChannel.Email])]));
    }
    [Fact]
    public void AccountScopeExpansionRequiresConfirmation()
    {
        var id = Guid.NewGuid(); ReminderRule[] old = [new(ResetKind.Weekly, true, [60], [ReminderChannel.Email], [id])];
        Assert.False(ApplicationCommands.ExpandsExternalRules(old, old));
        Assert.True(ApplicationCommands.ExpandsExternalRules(old, [old[0] with { AccountIds = null }]));
        Assert.False(ApplicationCommands.ExpandsExternalRules([old[0] with { AccountIds = null }], old));
    }
    [Theory] [InlineData(300)] [InlineData(360)] [InlineData(1440)]
    public void ShortQuotaRejectsLongLead(int lead) => Assert.Throws<ArgumentException>(() => ApplicationCommands.ValidateRules([new(ResetKind.Short, true, [lead], [ReminderChannel.Windows])]));
    [Fact]
    public void PhonePermissionsIncludeQuietHoursAndTimezone()
    {
        var p = new PhonePolicy(); Assert.False(ApplicationCommands.ExpandsPhone(p, p with { SmsPerDay = 3 }));
        Assert.True(ApplicationCommands.ExpandsPhone(p, p with { SmsPerDay = 6 }));
        Assert.True(ApplicationCommands.ExpandsPhone(p, p with { QuietEnabled = false }));
        Assert.True(ApplicationCommands.ExpandsPhone(p, p with { TimeZoneId = "UTC" }));
    }
    [Fact]
    public async Task UpdateWaitsForMcpExecutableLockAndLeavesOriginalIntact()
    {
        var dir = Temp(); var path = Path.Combine(dir, "CodexTracker.exe"); File.WriteAllText(path, "original");
        try
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAsync<IOException>(() => UpdateInstaller.WaitUntilReleasedAsync(path, TimeSpan.FromMilliseconds(150), default));
            Assert.Equal("original", File.ReadAllText(path));
            await UpdateInstaller.WaitUntilReleasedAsync(path, TimeSpan.FromSeconds(1), default);
        }
        finally { Clean(dir); }
    }
    private static string Temp() { var dir = Path.Combine(Path.GetTempPath(), "CodexTrackerAssistantTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); return dir; }
    [Fact]
    public async Task ApprovalIsRecheckedAfterWaitingBehindAnotherSend()
    {
        var dir = Temp();
        try
        {
            var secrets = new NotificationSecretStore(dir);
            secrets.Save(NotificationSecrets.Empty with { SendGrid = new(true, "fictional", "sender@example.test", "to@example.test") });
            var handler = new BlockingMail(); using var http = new System.Net.Http.HttpClient(handler);
            var journal = new ReminderJournal(dir);
            var dispatcher = new ReminderDispatcher(journal, secrets, new(http), () => new([], null), () => [], () => new(), () => new Dictionary<string, DateTimeOffset>(), _ => new(DeliveryStatus.Accepted, "Transmis au faux adaptateur Windows."));
            var first = dispatcher.TestAsync(ReminderChannel.Email);
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            bool authorized = true;
            var second = dispatcher.TestAsync(ReminderChannel.Email, stillAuthorized: () => authorized);
            authorized = false; handler.Release.SetResult();
            await first;
            Assert.Equal(DeliveryStatus.Skipped, (await second).Status);
            Assert.Equal(1, handler.Calls); Assert.Single(journal.Entries);
        }
        finally { Clean(dir); }
    }
    private sealed class BlockingMail : System.Net.Http.HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        { Calls++; Entered.TrySetResult(); await Release.Task.WaitAsync(token); return new(System.Net.HttpStatusCode.Accepted); }
    }
    private static void Clean(string dir)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexTrackerAssistantTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(dir).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe cleanup");
        Directory.Delete(dir, true);
    }
}
