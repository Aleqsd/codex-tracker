using CodexTracker.Codex;
using Xunit;

namespace CodexTracker.Tests;

public sealed class ClaudeTerminalStatusTests
{
    [Fact]
    public void StatusReportsTheSettingAndTheLastReadingWithoutReadingQuotas()
    {
        using var directory = new TestDirectory();
        var executable = directory.File("CodexTracker.exe");
        var collector = new ClaudeCodeObservations(directory.File("tracker"), ClaudeFixture.Location(directory));
        Assert.Equal(new ClaudeCodeObservations.TerminalStatus(false, null), collector.ReadStatus(executable));

        File.WriteAllText(directory.File("settings.json"), "{\"statusLine\":{\"type\":\"command\",\"command\":\"node other-tool.js\"}}");
        Assert.False(collector.ReadStatus(executable).Configured);
        File.WriteAllText(directory.File("settings.json"), ClaudeCodeObservations.Configuration(executable));
        Assert.True(collector.ReadStatus(executable).Configured);
        File.WriteAllText(directory.File("settings.json"), "{\"statusLine\":{\"type\":\"command\",\"command\":\"my-script.ps1 | CodexTracker.exe --claude-statusline\"}}");
        Assert.True(collector.ReadStatus(executable).Configured);

        var inbox = Directory.CreateDirectory(Path.Combine(directory.File("tracker"), "claude-observations")).FullName;
        File.WriteAllText(Path.Combine(inbox, "session-1.json"), "{}");
        Assert.Null(collector.ReadStatus(executable).LastReading);
        var reading = Path.Combine(inbox, "account-1.json");
        File.WriteAllText(reading, "not parsed");
        var written = new DateTime(2026, 10, 9, 8, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(reading, written);
        Assert.Equal(new DateTimeOffset(written), collector.ReadStatus(executable).LastReading);
    }
}
