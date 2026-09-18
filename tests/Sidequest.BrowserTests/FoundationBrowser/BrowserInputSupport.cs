using Microsoft.Playwright;

namespace Sidequest.BrowserTests.FoundationBrowser;

/// <summary>Models user input only after the target can receive interaction, including through an inherited inert gate.</summary>
internal static class BrowserInputSupport
{
    internal static async Task FillWhenActionableAsync(this ILocator input, string value)
    {
        await input.ClickAsync(new() { Trial = true });
        await input.FillAsync(value);
    }
}
