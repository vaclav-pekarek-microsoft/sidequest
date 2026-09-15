using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Separates ordinary intercepted sign-in from the explicitly tested native pre-initializer fallback.</summary>
internal static class SyntheticSignInSupport
{
    /// <summary>Waits for existing bridge UI that can only render after App's submit interceptor is installed; does not activate storage or continue an authentication failure.</summary>
    /// <param name="page">The ordinary synthetic sign-in page before its native submit button is clicked.</param>
    /// <returns>The existing web-first assertion, with its standard timeout and no mutation or retry.</returns>
    internal static Task WaitForInterceptorAsync(IPage page) =>
        Expect(page.Locator("[data-connection]"))
            .ToHaveTextAsync("Connected — actions still require current server authorization.");
}
