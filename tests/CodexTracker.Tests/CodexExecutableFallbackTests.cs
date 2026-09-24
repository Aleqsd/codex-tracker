using System.ComponentModel;
using System.Diagnostics;
using CodexTracker.Codex;
using Xunit;

namespace CodexTracker.Tests;

public sealed class CodexExecutableFallbackTests
{
    [Fact]
    public void DiscoveryKeepsLocalCopiesAfterPathAndDeduplicatesWithoutExecutingAnything()
    {
        using var directory = new TestDirectory();
        var local = directory.File("local");
        var pathDirectory = directory.File("msix");
        var pathExe = Fixture(Path.Combine(pathDirectory, "codex.exe"));
        var older = Fixture(Path.Combine(local, "OpenAI", "Codex", "bin", "old", "codex.exe"));
        var newer = Fixture(Path.Combine(local, "OpenAI", "Codex", "bin", "new", "codex.exe"));
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 2, 1));
        var candidates = CodexLocator.FindExecutables($"invalid\0path;{pathDirectory};{pathDirectory.ToUpperInvariant()}", local, directory.File("roaming"));
        Assert.Equal([pathExe, newer, older], candidates);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(126)]
    [InlineData(193)]
    [InlineData(216)]
    [InlineData(740)]
    public void UnlaunchableExecutableFallsBackAndStopsAfterFirstSuccessfulStart(int error)
    {
        using var process = new Process();
        var attempts = new List<string>();
        var result = CodexLocator.StartProcess(["blocked", "usable", "unused"], path =>
        {
            attempts.Add(path);
            if (path == "blocked") throw new Win32Exception(error, "private path must not leak");
            return process;
        });
        Assert.Same(process, result);
        Assert.Equal(["blocked", "usable"], attempts);
    }

    [Fact]
    public void ExhaustedCandidatesReportLaunchFailureWithoutPathsAndKeepAccessFailureOverMissingFile()
    {
        var error = Assert.Throws<TrackerException>(() => CodexLocator.StartProcess(["private-blocked", "removed"], path =>
            throw new Win32Exception(path == "removed" ? 2 : 5, $"secret location {path}")));
        Assert.Equal(CodexFailureCode.CodexLaunchFailed, error.Code);
        Assert.Contains("erreur 5", error.Message);
        Assert.DoesNotContain("private", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingCandidatesHaveAnActionableMissingServiceError(bool disappearsAfterDiscovery)
    {
        var error = Assert.Throws<TrackerException>(() => CodexLocator.StartProcess(
            disappearsAfterDiscovery ? ["removed"] : [], _ => throw new Win32Exception(2)));
        Assert.Equal(CodexFailureCode.CodexNotFound, error.Code);
    }

    [Fact]
    public void CancellationAndNonLaunchErrorsDoNotTryOtherExecutables()
    {
        foreach (var error in new Exception[] { new OperationCanceledException(),
            new TrackerException("protocol fixture", CodexFailureCode.ProtocolUnsupported), new Win32Exception(1223) })
        {
            var attempts = 0;
            var actual = Record.Exception(() => CodexLocator.StartProcess(["first", "second"], _ => { attempts++; throw error; }));
            Assert.Same(error, actual);
            Assert.Equal(1, attempts);
        }
    }

    private static string Fixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fictional executable; never started");
        return path;
    }
}
