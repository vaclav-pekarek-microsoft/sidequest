using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

internal static class ExperienceBrowserSupport
{
    private const string ConfirmationDiagnosticsScript = """
        () => {
            const main = document.querySelector('main');
            const management = document.querySelector('#management-title');
            const state = element => element === null ? null : {
                hidden: element.hidden,
                inert: element.inert,
                display: getComputedStyle(element).display,
                visibility: getComputedStyle(element).visibility
            };
            return JSON.stringify({
                path: location.pathname.slice(0, 180),
                online: navigator.onLine,
                connection: (document.querySelector('[data-connection]')?.textContent ?? '').slice(0, 240),
                reconnect: (document.querySelector('#components-reconnect-modal')?.className ?? '').slice(0, 120),
                alerts: Array.from(document.querySelectorAll('main [role="alert"], [data-authentication-warning]'))
                    .slice(0, 4).map(element => (element.textContent ?? '').slice(0, 320)),
                main: state(main),
                management: state(management),
                fileInputs: Array.from(document.querySelectorAll('input[type="file"]')).slice(0, 4).map(element => ({
                    connected: element.isConnected,
                    disabled: element.disabled,
                    initialized: typeof element._blazorInputFileNextFileId === 'number'
                }))
            });
        }
        """;

    internal const string StoreModule = "/experience/snapshot-store.js?v=1";
    internal const string RefreshModule = "/experience/refresh.js?v=1";
    internal const string BridgeModule = "/Components/Experience/ConnectionStatus.razor.js";

    internal static Task<IBrowserContext> WorkerContextAsync(FoundationBrowserFixture fixture) =>
        fixture.CreateContextAsync(width: 360, allowServiceWorkers: true);

    internal static async Task<IPage> SignInAsync(IBrowserContext context, string persona = "Alice")
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync("/signin");
        var home = new Uri(new Uri(page.Url), "/").AbsoluteUri;
        await SyntheticLoginDiagnostics.ObserveAsync(page, SyntheticAppSettings.Parse(home), "Experience", async () =>
        {
            var select = page.Locator("select#persona");
            await Expect(select).ToBeVisibleAsync();
            var value = await select.GetByRole(AriaRole.Option,
                new() { NameRegex = new($"^{Regex.Escape(persona)}\\b") }).GetAttributeAsync("value");
            Assert.False(string.IsNullOrEmpty(value));
            await select.SelectOptionAsync(value!);
            await SyntheticSignInSupport.WaitForInterceptorAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with synthetic identity", Exact = true }).ClickAsync();
            await page.WaitForURLAsync(home, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
            await Expect(page.Locator("[data-snapshot]")).ToContainTextAsync("Joined basics saved");
            await Expect(page.Locator("[data-refresh]")).ToBeEnabledAsync();
        });
        return page;
    }

    internal static async Task<Guid> CreateEventAsync(IPage page, DateOnly? day = null, string? timeZoneId = null)
    {
        await page.GotoAsync("/events/create");
        var name = page.GetByRole(AriaRole.Textbox, new() { NameRegex = new("^Name \\(3") });
        await Expect(name).ToBeEditableAsync();
        await name.FillWhenActionableAsync($"Offline Event {Guid.NewGuid():N}");
        var start = day ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
        await page.GetByLabel("Start date, inclusive", new() { Exact = true }).FillWhenActionableAsync(start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await page.GetByLabel("End date, inclusive", new() { Exact = true }).FillWhenActionableAsync(start.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (timeZoneId is not null)
            await page.GetByRole(AriaRole.Combobox, new() { Name = "IANA time zone", Exact = true }).SelectOptionAsync(timeZoneId);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save Draft", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/events/[0-9a-f-]{36}$"));
        var id = Guid.Parse(new Uri(page.Url).Segments[^1]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Publish Event", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Create Quest", Exact = true })).ToBeVisibleAsync();
        return id;
    }

    internal static async Task<Guid> CreateQuestAsync(IPage page, Guid eventId, string title, bool privateQuest = false)
    {
        await page.GotoAsync($"/quests/create?eventId={eventId}");
        var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true });
        await Expect(input).ToBeEditableAsync();
        await input.FillWhenActionableAsync(title);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Description (plain text)", Exact = true }).FillWhenActionableAsync("NEVER-SAVE-THIS-DESCRIPTION");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Location (required to publish)", Exact = true }).FillWhenActionableAsync("Synthetic meeting point");
        await page.GetByRole(AriaRole.Combobox, new() { NameRegex = new("^Visibility\\b") }).SelectOptionAsync(privateQuest ? "Private" : "Public");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/quests/[0-9a-f-]{36}$"));
        var id = Guid.Parse(new Uri(page.Url).Segments[^1]);
        await ConfirmAsync(page, "Publish draft");
        await Expect(page.Locator("p[role='status'] > strong")).ToHaveTextAsync("Active");
        return id;
    }

    internal static async Task ConfirmAsync(IPage page, string action)
    {
        try
        {
            await SelectQuestActionAsync(page, action);
            var reason = page.GetByRole(AriaRole.Textbox, new() { NameRegex = new("^Reason \\(") });
            if (await reason.CountAsync() > 0)
                await reason.FillWhenActionableAsync("Synthetic offline acceptance.");
            await page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^I confirm this action") }).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = $"Confirm: {action}", Exact = true }).ClickAsync();
            await Expect(page.GetByText("Change saved. Required delivery will be attempted durably.", new() { Exact = true })).ToBeVisibleAsync();
            await Expect(page.Locator(".management-confirmation")).ToHaveCountAsync(0);
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            await ReportConfirmationFailureAsync(page, action);
            throw;
        }
    }

    internal static async Task SelectQuestActionAsync(IPage page, string action)
    {
        if (await page.GetByRole(AriaRole.Button, new() { Name = $"Confirm: {action}", Exact = true }).CountAsync() > 0)
            return;
        if (action is "Cancel Quest" or "Delete draft")
            await page.Locator("details.danger-zone > summary").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = action, Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = $"Confirm: {action}", Exact = true })).ToBeVisibleAsync();
    }

    private static async Task ReportConfirmationFailureAsync(IPage page, string action)
    {
        try
        {
            var state = await page.EvaluateAsync<string>(ConfirmationDiagnosticsScript);
            await Console.Out.WriteLineAsync($"Experience confirmation '{action}' failed: {state}");
        }
        catch (Exception error) when (error is PlaywrightException or TimeoutException)
        {
            await Console.Out.WriteLineAsync(
                $"Experience confirmation '{action}' diagnostics unavailable ({error.GetType().Name}); original failure retained.");
        }
    }

    internal static async Task RefreshAsync(IPage page)
    {
        // Use the bridge's serialized refresh path, rather than racing its automatic
        // refresh with a separate ticket issued directly through the storage module.
        var options = page.Locator("[data-device-tools]");
        if (await options.GetAttributeAsync("open") is null)
            await options.Locator("summary").ClickAsync();
        var button = page.Locator("[data-refresh]");
        await Expect(button).ToBeEnabledAsync();
        var endpoint = new Uri(new Uri(page.Url), "/experience/joined-snapshot").AbsoluteUri;
        var response = await page.RunAndWaitForResponseAsync(() => button.ClickAsync(),
            response => response.Url == endpoint && response.Request.Method == "GET");
        Assert.Equal(200, response.Status);
        // The bridge reports success and enables the button after response validation
        // and snapshot commit; a separate transport-finished task is not that contract.
        await Expect(button).ToBeEnabledAsync();
        await Expect(page.Locator("[data-snapshot]")).ToContainTextAsync("Joined basics saved");
    }

    internal static object Snapshot(string title = "Saved joined Quest") => new
    {
        accountId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        refreshedUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture),
        quests = new[] { new {
            id = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            eventId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            title,
            location = "Synthetic room",
            startUtc = "2026-10-01T10:00:00Z",
            endUtc = "2026-10-01T11:00:00Z",
            timeZoneId = "Europe/Prague",
            status = 1
        } }
    };

    internal static Task SaveAsync(IPage page, object snapshot) => page.EvaluateAsync("""
        async value => {
            const store = await import('/experience/snapshot-store.js?v=1');
            const ticket = await store.beginRefresh(await store.currentEpoch());
            await store.replaceSnapshot(ticket, value);
        }
        """, snapshot);
}
