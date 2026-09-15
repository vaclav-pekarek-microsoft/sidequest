using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

internal static class ExperienceBrowserSupport
{
    internal const string StoreModule = "/experience/snapshot-store.js?v=1";
    internal const string RefreshModule = "/experience/refresh.js?v=1";
    internal const string BridgeModule = "/Components/Experience/ConnectionStatus.razor.js";

    internal static Task<IBrowserContext> WorkerContextAsync(FoundationBrowserFixture fixture) =>
        fixture.CreateContextAsync(width: 360, allowServiceWorkers: true);

    internal static async Task<IPage> SignInAsync(IBrowserContext context, string persona = "Alice")
    {
        var page = await context.NewPageAsync();
        await page.GotoAsync("/signin");
        var select = page.Locator("select#persona");
        await Expect(select).ToBeVisibleAsync();
        var value = await select.GetByRole(AriaRole.Option,
            new() { NameRegex = new($"^{Regex.Escape(persona)}\\b") }).GetAttributeAsync("value");
        Assert.False(string.IsNullOrEmpty(value));
        await select.SelectOptionAsync(value!);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with synthetic identity", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator("[data-snapshot]")).ToContainTextAsync("Joined basics saved");
        await Expect(page.Locator("[data-refresh]")).ToBeEnabledAsync();
        return page;
    }

    internal static async Task<Guid> CreateEventAsync(IPage page)
    {
        await page.GotoAsync("/events/create");
        var name = page.GetByRole(AriaRole.Textbox, new() { NameRegex = new("^Name \\(3") });
        await Expect(name).ToBeEditableAsync();
        await name.FillAsync($"Offline Event {Guid.NewGuid():N}");
        var date = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await page.GetByLabel("Start date, inclusive", new() { Exact = true }).FillAsync(date);
        await page.GetByLabel("End date, inclusive", new() { Exact = true }).FillAsync(date);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save Draft or changes", Exact = true }).ClickAsync();
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
        await input.FillAsync(title);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Description (plain text)", Exact = true }).FillAsync("NEVER-SAVE-THIS-DESCRIPTION");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Location (required to publish)", Exact = true }).FillAsync("Synthetic meeting point");
        await page.GetByRole(AriaRole.Combobox, new() { NameRegex = new("^Visibility\\b") }).SelectOptionAsync(privateQuest ? "Private" : "Public");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/quests/[0-9a-f-]{36}$"));
        var id = Guid.Parse(new Uri(page.Url).Segments[^1]);
        await ConfirmAsync(page, "Publish draft");
        return id;
    }

    internal static async Task ConfirmAsync(IPage page, string action)
    {
        var reason = page.GetByRole(AriaRole.Textbox, new() { NameRegex = new("^Reason \\(") });
        await reason.FillAsync("Synthetic offline acceptance.");
        await page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^I confirm this action") }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = action, Exact = true }).ClickAsync();
        await Expect(reason).ToHaveValueAsync("");
    }

    internal static Task RefreshAsync(IPage page) => page.EvaluateAsync("""
        async () => {
            const store = await import('/experience/snapshot-store.js?v=1');
            const refresh = await import('/experience/refresh.js?v=1');
            await refresh.refreshJoined(await store.currentEpoch());
        }
        """);

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
