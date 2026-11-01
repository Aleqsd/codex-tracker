using CodexTracker.App;
using Xunit;

namespace CodexTracker.Tests;

public sealed class BugReportTests
{
    [Fact]
    public void TheIssueIsPrefilledWithTheDiagnosticInACodeBlock()
    {
        var url = SupportDiagnostics.ReportUrl("Codex Tracker 0.9.42\nWindows : 10.0.26200");
        Assert.StartsWith(SupportDiagnostics.IssuesPage + "?title=", url);
        var body = Uri.UnescapeDataString(url[(url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.Contains("```\nCodex Tracker 0.9.42\nWindows : 10.0.26200\n```", body);
    }

    [Fact]
    public void ALongDiagnosticIsCutToStayWithinBrowserLimits()
    {
        var url = SupportDiagnostics.ReportUrl(string.Join("\n", Enumerable.Repeat("Ligne de diagnostic accentuée é è à", 600)));
        Assert.True(url.Length <= 7000, $"URL de {url.Length} caractères");
        Assert.Contains("…", Uri.UnescapeDataString(url));
    }
}
