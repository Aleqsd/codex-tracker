using CodexTracker.Codex;
using CodexTracker.Core;
using Xunit;

namespace CodexTracker.Tests;

/// <summary>Claude Code removes a window from its status line once its reset passes; Ultra never reports one.</summary>
public sealed class ClaudeDroppedWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static AccountSnapshot Reading(DateTimeOffset at, double weeklyUsed, QuotaWindow? fiveHours = null) =>
        new("plan@example.test", "max", [new("claude", "Claude Code",
            fiveHours is null ? [new(weeklyUsed, 10080, Now.AddDays(3))] : [fiveHours, new(weeklyUsed, 10080, Now.AddDays(3))])], null, null, at);

    [Fact]
    public void AFiveHourWindowDroppedAtItsResetStaysAsLastMeasured()
    {
        var measured = new QuotaWindow(40, 300, Now.AddMinutes(-5));
        var kept = ClaudeCodeObservations.KeepDroppedShortWindow(Reading(Now, 30), Reading(Now.AddMinutes(-20), 30, measured));
        Assert.Equal(measured, kept.Short);
        Assert.Equal(30, kept.Weekly!.UsedPercent);
        Assert.True(new AccountState(new(Guid.NewGuid(), "plan@example.test", AccountProvider.ClaudeCode), kept).HasShortWindow);
    }

    [Fact]
    public void NewUseWithoutAFiveHourWindowMeansThePlanHasNone()
    {
        var measured = new QuotaWindow(40, 300, Now.AddMinutes(-5));
        var reading = Reading(Now, 31);
        Assert.Same(reading, ClaudeCodeObservations.KeepDroppedShortWindow(reading, Reading(Now.AddMinutes(-20), 30, measured)));
        Assert.False(new AccountState(new(Guid.NewGuid(), "plan@example.test", AccountProvider.ClaudeCode), reading).HasShortWindow);
    }

    [Fact]
    public async Task TheCollectorKeepsTheDroppedWindowUntilTheWeekMovesWithoutOne()
    {
        using var directory = new TestDirectory(); ClaudeFixture.SignIn(directory);
        var location = ClaudeFixture.Location(directory);
        var collector = new ClaudeCodeObservations(directory.File("tracker"), location);
        var session = Guid.NewGuid(); var start = DateTimeOffset.UtcNow.AddHours(-6);
        await collector.CaptureAsync(ClaudeFixture.Input(session), true, start);
        await collector.CaptureAsync(Input(session, 70, fiveHourReset: start.AddMinutes(10)), false, start);
        var identity = await ClaudeCodeIdentity.ReadAsync(location, default);

        await collector.CaptureAsync(Input(session, 70), false, start.AddMinutes(11));
        Assert.Equal(start.AddMinutes(10).ToUnixTimeSeconds(), collector.Read(identity).Short!.ResetsAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(start.AddMinutes(11), collector.Read(identity).FetchedAt);

        await collector.CaptureAsync(Input(session, 72), false, start.AddMinutes(12));
        Assert.Null(collector.Read(identity).Short);
        Assert.Equal(72, collector.Read(identity).Weekly!.UsedPercent);
    }

    private static string Input(Guid session, double weeklyUsed, DateTimeOffset? fiveHourReset = null) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            session_id = session,
            rate_limits = fiveHourReset is { } reset
                ? (object)new { five_hour = new { used_percentage = 40, resets_at = reset.ToUnixTimeSeconds() }, seven_day = new { used_percentage = weeklyUsed, resets_at = Now.AddDays(3).ToUnixTimeSeconds() } }
                : new { seven_day = new { used_percentage = weeklyUsed, resets_at = Now.AddDays(3).ToUnixTimeSeconds() } }
        });

    [Fact]
    public void OnlyAPassedResetExplainsAMissingFiveHourWindow()
    {
        var reading = Reading(Now, 30);
        // Still running, or without a date: its disappearance is not a reset.
        Assert.Same(reading, ClaudeCodeObservations.KeepDroppedShortWindow(reading, Reading(Now.AddMinutes(-20), 30, new(40, 300, Now.AddMinutes(5)))));
        Assert.Same(reading, ClaudeCodeObservations.KeepDroppedShortWindow(reading, Reading(Now.AddMinutes(-20), 30, new(40, 300, null))));
        Assert.Same(reading, ClaudeCodeObservations.KeepDroppedShortWindow(reading, Reading(Now.AddMinutes(-20), 30)));
        // A reading that has its own 5-hour window is never altered.
        var fresh = Reading(Now, 30, new(2, 300, Now.AddHours(5)));
        Assert.Same(fresh, ClaudeCodeObservations.KeepDroppedShortWindow(fresh, Reading(Now.AddMinutes(-20), 30, new(40, 300, Now.AddMinutes(-5)))));
    }
}
