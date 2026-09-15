using Microsoft.Playwright;

namespace Sidequest.BrowserTests.FoundationBrowser;

/// <summary>Owns a headless Chromium process exclusively for parent-managed GitHub Actions compatibility scenarios.</summary>
/// <remarks>Requires a running synthetic loopback app and refuses local browser execution; it never starts a server or manages the application's database.</remarks>
/// <remarks>Do not race initialization or disposal with context creation. Each test owns its context and page, and xUnit releases the shared fixture only after those sessions finish.</remarks>
public sealed class FoundationBrowserFixture : IAsyncLifetime
{
    private IPlaywright? playwright;
    private IBrowser? browser;
    /// <summary>Gets the validated synthetic-app origin after successful fixture initialization.</summary>
    internal SyntheticAppSettings Settings { get; private set; } = null!;

    /// <inheritdoc/>
    /// <remarks>Validates configuration and the GitHub Actions execution boundary before starting bundled Chromium.</remarks>
    /// <exception cref="InvalidOperationException">The loopback base URL is invalid or execution is outside the required CI environment.</exception>
    /// <exception cref="PlaywrightException">Playwright initialization or Chromium launch fails; no alternate browser is attempted.</exception>
    public async Task InitializeAsync()
    {
        Settings = SyntheticAppSettings.Parse(Environment.GetEnvironmentVariable("SIDEQUEST_BASE_URL"));
        if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Foundation browser execution requires the parent-managed GitHub Actions Chromium job. Do not bypass managed local browser policy.");

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    /// <summary>Creates a fresh isolated browser session with deterministic locale/zone and off-origin HTTP requests blocked.</summary>
    /// <param name="width">The viewport width in CSS pixels; height is fixed at 800 CSS pixels.</param>
    /// <param name="allowServiceWorkers">Explicit opt-in for offline/PWA scenarios; all other contexts block service workers.
    /// Context-level origin routing remains installed for worker-originated network requests.</param>
    /// <returns>A new context whose cookies/storage are not shared and which the caller must asynchronously dispose.</returns>
    /// <exception cref="PlaywrightException">The browser cannot create or configure the isolated context.</exception>
    /// <example>
    /// <code>
    /// await using var context = await fixture.CreateContextAsync(width: 360);
    /// var page = await context.NewPageAsync();
    /// await page.GotoAsync("/foundation");
    /// await Assertions.Expect(page.Locator("select#persona")).ToBeVisibleAsync();
    /// </code>
    /// </example>
    internal async Task<IBrowserContext> CreateContextAsync(int width = 1280, bool allowServiceWorkers = false)
    {
        var context = await browser!.NewContextAsync(CreateOptions(Settings, width, allowServiceWorkers));
        // No off-origin HTTP request (including Entra redirects/CDN assets) may leave this test.
        await context.RouteAsync("**/*", route =>
            Settings.IsSameOrigin(route.Request.Url) ? route.ContinueAsync() : route.AbortAsync());
        return context;
    }

    /// <summary>Builds deterministic isolated-context options without launching a browser or bypassing fixture initialization.</summary>
    internal static BrowserNewContextOptions CreateOptions(SyntheticAppSettings settings, int width = 1280,
        bool allowServiceWorkers = false) => new()
        {
            BaseURL = settings.BaseUri.AbsoluteUri,
            ViewportSize = new() { Width = width, Height = 800 },
            Locale = "en-US",
            TimezoneId = "Europe/Prague",
            ServiceWorkers = allowServiceWorkers ? ServiceWorkerPolicy.Allow : ServiceWorkerPolicy.Block
        };

    /// <inheritdoc/>
    /// <remarks>Closes this fixture's browser and always disposes its Playwright instance; no managed browser/profile is touched.</remarks>
    /// <exception cref="PlaywrightException">Closing the owned browser fails.</exception>
    public async Task DisposeAsync()
    {
        try
        {
            if (browser is not null)
                await browser.CloseAsync();
        }
        finally
        {
            playwright?.Dispose();
        }
    }
}
