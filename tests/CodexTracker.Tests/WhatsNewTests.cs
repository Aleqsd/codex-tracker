using CodexTracker.App;
using Xunit;

namespace CodexTracker.Tests;

public sealed class WhatsNewTests
{
    [Fact]
    public void AFreshInstallationOrAnUpToDateOneHasNothingNew()
    {
        Assert.Empty(WhatsNew.Since(null, "0.9.42"));
        Assert.Empty(WhatsNew.Since("0.9.42", "0.9.42"));
        Assert.Empty(WhatsNew.Since("1.0.0", "0.9.42"));
    }

    [Fact]
    public void AnUpdateShowsEveryFeatureSinceTheLastVersionSeenNewestFirst()
    {
        var items = WhatsNew.Since("0.9.38", "0.9.42");
        Assert.NotEmpty(items);
        Assert.All(items, i => Assert.InRange(WhatsNew.Parse(i.Version)!, new Version(0, 9, 39), new Version(0, 9, 42)));
        Assert.Equal("0.9.42", items[0].Version);
        Assert.DoesNotContain(items, i => i.Version == "0.9.38");
        Assert.Equal(new Version(0, 9, 42), WhatsNew.Parse("0.9.42-beta.1+abc"));
    }
}
