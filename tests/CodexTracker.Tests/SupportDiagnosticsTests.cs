using CodexTracker.App;
using CodexTracker.Codex;
using Xunit;

namespace CodexTracker.Tests;

public sealed class SupportDiagnosticsTests
{
    [Fact]
    public void ReportUsesAllowlistWithoutAccountNamesPathsOrVersionMetadata()
    {
        var preferences = new TrackerPreferences
        {
            Appearances = new() { [Guid.NewGuid()] = new("private@example.test", @"C:\Users\private\picture.png") },
            SentExpiryReminders = new() { ["private-credit-key"] = DateTimeOffset.UtcNow },
            RecoveryPending = true, IncludePrereleaseUpdates = true
        };
        var report = SupportDiagnostics.Create("0.9.0-private+secret", preferences,
            new(CodexHomeSource.Environment, CodexSessionStatus.Expired, CodexServiceStatus.Unavailable,
                CodexFailureCode.SessionExpired, DateTimeOffset.Parse("2026-09-21T10:00:00+02:00"), null),
            false, false, WindowsNotificationState.Busy, false, false);
        Assert.DoesNotContain("private", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", report);
        Assert.DoesNotContain(@"C:\", report);
        Assert.Contains("SessionExpired", report);
        Assert.Contains("21/09/2026 08:00:00 UTC", report);
        Assert.Contains("Préférences à vérifier : oui", report);
        Assert.Contains("Dernier relevé réussi : inconnue", report);
    }

    [Fact]
    public void DemoAndUnknownStatesNeverClaimCompatibilityOrNotificationDelivery()
    {
        var report = SupportDiagnostics.Create("unknown-version-token", new(), null, true, false,
            (WindowsNotificationState)1234, false, true);
        Assert.Contains("démonstration", report);
        Assert.Contains("non vérifiée", report);
        Assert.Contains("Unknown", report);
        Assert.DoesNotContain("unknown-version-token", report);
        Assert.DoesNotContain("1234", report);
    }
}
