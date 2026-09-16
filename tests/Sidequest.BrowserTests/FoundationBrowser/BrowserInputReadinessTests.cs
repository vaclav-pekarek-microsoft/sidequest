using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.FoundationBrowser;

/// <summary>Verifies native browser input actionability through an inert shadow-DOM ancestor in an isolated synthetic document.</summary>
/// <param name="fixture">The existing CI-only Chromium fixture; no application guard or authorization state is modified by this test.</param>
public sealed class BrowserInputReadinessTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    /// <summary>Editable is insufficient for inert text inputs; guarded filling rejects blocked interaction and fills after the fixture gate opens without clicking.</summary>
    /// <param name="kind">Native text, textarea, or datetime-local control inside an open shadow root.</param>
    /// <param name="value">Valid input text or local date-time value to enter after the synthetic gate opens.</param>
    /// <param name="unguardedValue">The native FillAsync result while inert: empty for keyboard-filled text, but assigned directly for datetime-local.</param>
    /// <returns>Completion after the unguarded control, blocked-action failure, and successful guarded fill with no click events.</returns>
    [Theory]
    [InlineData("text", "Receivable text", "")]
    [InlineData("textarea", "Receivable description", "")]
    [InlineData("datetime-local", "2027-01-15T20:30", "2027-01-15T20:30")]
    public async Task GuardedFillRequiresReceivableEventsWithoutClicking(string kind, string value, string unguardedValue)
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.SetContentAsync("<div id='gate' inert><div id='host'></div></div>");
        await page.EvaluateAsync("""
            kind => {
                const root = document.querySelector('#host').attachShadow({ mode: 'open' });
                const label = document.createElement('label');
                label.htmlFor = 'probe';
                label.textContent = 'Synthetic value';
                const input = document.createElement(kind === 'textarea' ? 'textarea' : 'input');
                if (kind !== 'textarea') input.type = kind;
                input.id = 'probe';
                input.dataset.clicks = '0';
                input.addEventListener('click', () => input.dataset.clicks = String(Number(input.dataset.clicks) + 1));
                root.append(label, input);
            }
            """, kind);
        var input = page.GetByLabel("Synthetic value", new() { Exact = true });
        await Expect(page.Locator("#gate")).ToHaveJSPropertyAsync("inert", true);
        await Expect(input).ToBeEditableAsync();

        // Deliberate negative control: exercise Playwright's unguarded fill, not application input.
        await input.FillAsync(value);
        await Expect(input).ToHaveValueAsync(unguardedValue);
        await input.EvaluateAsync("element => element.value = ''");
        page.SetDefaultTimeout(1_000);
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => input.FillWhenActionableAsync(value));
        page.SetDefaultTimeout(30_000);
        Assert.Contains("Timeout", failure.Message, StringComparison.Ordinal);
        await Expect(input).ToHaveValueAsync("");
        await Expect(input).ToHaveAttributeAsync("data-clicks", "0");

        await page.Locator("#gate").EvaluateAsync("element => element.inert = false");
        await input.FillWhenActionableAsync(value);
        await Expect(input).ToHaveValueAsync(value);
        await Expect(input).ToHaveAttributeAsync("data-clicks", "0");
    }
}
