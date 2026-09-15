using Microsoft.Playwright;

namespace Sidequest.BrowserTests.FoundationBrowser;

/// <summary>Checks explicit offline-test opt-in without launching a browser or weakening the CI-only execution boundary.</summary>
public sealed class BrowserContextOptionsTests
{
    /// <summary>Retains blocked service workers and isolated default storage when existing callers omit the new option.</summary>
    [Fact]
    public void DefaultOptions_BlockServiceWorkers_WithoutSharedStorage()
    {
        var settings = SyntheticAppSettings.Parse("http://127.0.0.1:5078");

        var options = FoundationBrowserFixture.CreateOptions(settings);

        Assert.Equal("http://127.0.0.1:5078/", options.BaseURL);
        Assert.Equal(ServiceWorkerPolicy.Block, options.ServiceWorkers);
        var viewport = Assert.IsType<ViewportSize>(options.ViewportSize);
        Assert.Equal(1280, viewport.Width);
        Assert.Equal(800, viewport.Height);
        Assert.Null(options.StorageState);
        Assert.Null(options.StorageStatePath);
    }

    /// <summary>Changes only the explicitly selected worker policy while preserving mobile viewport, deterministic locale/zone and fresh storage.</summary>
    /// <param name="allowServiceWorkers">The caller's explicit opt-in or opt-out.</param>
    /// <param name="expectedPolicy">The independently specified Playwright policy.</param>
    [Theory]
    [InlineData(false, ServiceWorkerPolicy.Block)]
    [InlineData(true, ServiceWorkerPolicy.Allow)]
    public void ExplicitWorkerPolicy_PreservesDeterministicMobileContext(bool allowServiceWorkers, ServiceWorkerPolicy expectedPolicy)
    {
        var settings = SyntheticAppSettings.Parse("https://localhost:5443");

        var options = FoundationBrowserFixture.CreateOptions(settings, width: 360, allowServiceWorkers: allowServiceWorkers);

        Assert.Equal("https://localhost:5443/", options.BaseURL);
        Assert.Equal(expectedPolicy, options.ServiceWorkers);
        var viewport = Assert.IsType<ViewportSize>(options.ViewportSize);
        Assert.Equal(360, viewport.Width);
        Assert.Equal(800, viewport.Height);
        Assert.Equal("en-US", options.Locale);
        Assert.Equal("Europe/Prague", options.TimezoneId);
        Assert.Null(options.StorageState);
        Assert.Null(options.StorageStatePath);
    }
}
