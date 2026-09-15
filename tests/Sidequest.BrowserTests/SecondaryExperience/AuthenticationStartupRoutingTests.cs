using Sidequest.BrowserTests.FoundationBrowser;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Verifies the startup barrier's exact asset family and origin without creating a browser or weakening fixture routing.</summary>
public sealed class AuthenticationStartupRoutingTests
{
    /// <summary>Accepts logical and correctly fingerprinted App modules only; other scripts, origins and URL decorations cannot trigger the barrier.</summary>
    /// <param name="url">A synthetic candidate request URL.</param>
    /// <param name="expected">Whether the narrowly scoped App-module barrier should intercept the request.</param>
    [Theory]
    [InlineData("http://127.0.0.1:5078/Components/App.razor.js", true)]
    [InlineData("http://127.0.0.1:5078/Components/App.nr20ghu6hy.razor.js", true)]
    [InlineData("http://127.0.0.1:5078/Components/App.a123b456c7.razor.js", true)]
    [InlineData("http://127.0.0.1:5078/Components/App.razor.nr20ghu6hy.js", false)]
    [InlineData("http://127.0.0.1:5078/Components/AppOther.nr20ghu6hy.razor.js", false)]
    [InlineData("http://127.0.0.1:5078/Components/App.nr20ghu6hy.razor.js.gz", false)]
    [InlineData("http://127.0.0.1:5078/Components/Experience/ConnectionStatus.nr20ghu6hy.razor.js", false)]
    [InlineData("http://127.0.0.1:5079/Components/App.nr20ghu6hy.razor.js", false)]
    [InlineData("https://127.0.0.1:5078/Components/App.nr20ghu6hy.razor.js", false)]
    [InlineData("http://localhost:5078/Components/App.nr20ghu6hy.razor.js", false)]
    [InlineData("http://user@127.0.0.1:5078/Components/App.nr20ghu6hy.razor.js", false)]
    [InlineData("http://127.0.0.1:5078/Components/App.nr20ghu6hy.razor.js?other=1", false)]
    [InlineData("http://127.0.0.1:5078/Components/App.nr20ghu6hy.razor.js\n", false)]
    public void InitializerBarrierMatchesOnlyAppAssetOnExactFixtureOrigin(string url, bool expected)
    {
        var settings = SyntheticAppSettings.Parse("http://127.0.0.1:5078");
        Assert.Equal(expected, AuthenticationStartupBrowserTests.InitializerRoute(settings).IsMatch(url));
    }
}
