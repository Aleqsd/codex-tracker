using CodexTracker.App;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

public sealed class WindowsNotificationTests
{
    [Theory]
    [InlineData(2, "plein écran")]
    [InlineData(3, "plein écran exclusif")]
    [InlineData(4, "présentation")]
    [InlineData(1, "inactive")]
    [InlineData(6, "temporairement")]
    public void ShellSuppressionIsExplainedWithoutSending(int state, string detail)
    {
        var sends = 0;
        var sender = new WindowsNotificationDelivery((_, _) => sends++, () => true, () => (WindowsNotificationState)state, () => true);
        var result = sender.Send("Test", "Exemple");
        Assert.Equal(DeliveryStatus.Skipped, result.Status);
        Assert.Contains(detail, result.Detail);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(7)]
    public void SubmissionDoesNotPromiseDisplay(int state)
    {
        var sends = new List<(string, string)>();
        var sender = new WindowsNotificationDelivery((title, body) => sends.Add((title, body)), () => true,
            () => (WindowsNotificationState)state, () => true);
        var result = sender.Send("Titre fictif", "Message fictif");
        Assert.Equal(DeliveryStatus.Accepted, result.Status);
        Assert.Contains("non confirmé", result.Detail);
        Assert.Contains("Ne pas déranger", result.Detail);
        Assert.Equal(("Titre fictif", "Message fictif"), Assert.Single(sends));
    }

    [Fact]
    public void ScheduledReminderCanBeDeferredUntilWindowsIsAvailable()
    {
        var state = WindowsNotificationState.Busy;
        var sends = 0;
        var sender = new WindowsNotificationDelivery((_, _) => sends++, () => true, () => state, () => true);
        Assert.Equal(DeliveryStatus.Deferred, sender.Send("Test", "Exemple", deferWhenBusy: true).Status);
        Assert.Equal(0, sends);
        state = WindowsNotificationState.Available;
        Assert.Equal(DeliveryStatus.Accepted, sender.Send("Test", "Exemple", deferWhenBusy: true).Status);
        Assert.Equal(1, sends);
    }

    [Fact]
    public void DisabledNotificationsAreNotReportedAsSent()
    {
        var sender = new WindowsNotificationDelivery((_, _) => throw new Exception("must not send"), () => true,
            () => WindowsNotificationState.Available, () => false);
        var result = sender.Send("Test", "Exemple");
        Assert.Equal(DeliveryStatus.Skipped, result.Status);
        Assert.Contains("désactivées", result.Detail);
    }

    [Fact]
    public void DisposedTrayIsAnExplicitFailure()
    {
        var sender = new WindowsNotificationDelivery((_, _) => throw new Exception("must not send"), () => false);
        Assert.Equal(DeliveryStatus.Failed, sender.Send("Test", "Exemple").Status);
    }

    [Fact]
    public void ShellFailureDoesNotLeakExceptionContent()
    {
        var sender = new WindowsNotificationDelivery((_, _) => throw new InvalidOperationException("private-example"),
            () => true, () => WindowsNotificationState.Available, () => true);
        var result = sender.Send("Test", "Exemple");
        Assert.Equal(DeliveryStatus.Failed, result.Status);
        Assert.DoesNotContain("private-example", result.Detail);
    }
}
