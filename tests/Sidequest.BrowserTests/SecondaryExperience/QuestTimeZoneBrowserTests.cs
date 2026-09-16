using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sidequest.BrowserTests.CoreBrowser;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Exercises inherited Event-zone input and persisted UTC/device-local rendering through the real synthetic browser application.</summary>
/// <param name="fixture">The existing CI-only browser fixture, with its actual device zone fixed to Europe/Prague.</param>
public sealed class QuestTimeZoneBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    private const string EventZone = "America/New_York";

    /// <summary>A same-Event-day Quest retains its inherited zone while its UTC instants and device-local display cross into the next date.</summary>
    /// <param name="month">January or July in the next calendar year, covering standard and daylight offsets without ambiguous local inputs.</param>
    /// <param name="utcStartHour">The exact next-day UTC hour corresponding to 20:30 in the Event zone.</param>
    /// <param name="eventOffset">Expected compact Event-zone offset in the rendered local timestamps.</param>
    /// <param name="deviceOffset">Expected compact device-zone offset, independently different from the Event zone.</param>
    /// <returns>Completion after creation, publication, read-only zone checks in both editors, and persisted timestamp assertions after reload.</returns>
    [Theory]
    [InlineData(1, 1, "-05", "+01")]
    [InlineData(7, 0, "-04", "+02")]
    public async Task InheritedEventZonePreservesUtcAndDifferentDeviceDay(
        int month, int utcStartHour, string eventOffset, string deviceOffset)
    {
        var day = new DateOnly(DateTime.UtcNow.Year + 1, month, 15);
        var date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var nextDate = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var startUtc = new DateTimeOffset(day.AddDays(1).ToDateTime(new TimeOnly(utcStartHour, 30)), TimeSpan.Zero);
        var endUtc = startUtc.AddHours(1);
        await using var context = await fixture.CreateContextAsync();
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        Assert.Equal("Europe/Prague", await page.EvaluateAsync<string>("() => Intl.DateTimeFormat().resolvedOptions().timeZone"));
        var eventId = await ExperienceBrowserSupport.CreateEventAsync(page, day, EventZone);

        await page.GotoAsync($"/quests/create?eventId={eventId}");
        var title = page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true });
        await Expect(title).ToBeEditableAsync();
        await AssertInheritedZoneAsync(page);
        await QuestCreationDiagnostics.ObserveAsync(page, title, async () =>
        {
            await title.FillAsync($"Zone proof {Guid.NewGuid():N}");
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Description (plain text)", Exact = true })
                .FillAsync("An evening Quest contained in the Event's one local date.");
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Location (required to publish)", Exact = true })
                .FillAsync("Synthetic evening meeting point");
            await page.GetByLabel("Starts in Event zone", new() { Exact = true }).FillAsync($"{date}T20:30");
            await page.GetByLabel("Ends in Event zone", new() { Exact = true }).FillAsync($"{date}T21:30");
            await Expect(page.GetByLabel("Start UTC offset (hours)", new() { Exact = true })).ToHaveValueAsync("");
            await Expect(page.GetByLabel("End UTC offset (hours)", new() { Exact = true })).ToHaveValueAsync("");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex("/quests/[0-9a-f-]{36}$"));
        });
        var questId = Guid.Parse(new Uri(page.Url).Segments[^1]);
        await AssertTimesAsync();
        await ExperienceBrowserSupport.ConfirmAsync(page, "Publish draft");
        await Expect(page.Locator("p[role='status'] > strong")).ToHaveTextAsync("Active");
        await AssertTimesAsync();

        await page.GotoAsync($"/quests/{questId}/edit");
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true })).ToBeEditableAsync();
        await AssertInheritedZoneAsync(page);
        await Expect(page.GetByLabel("Starts in Event zone", new() { Exact = true })).ToHaveValueAsync($"{date}T20:30");
        await Expect(page.GetByLabel("Ends in Event zone", new() { Exact = true })).ToHaveValueAsync($"{date}T21:30");
        await page.GotoAsync($"/quests/{questId}");
        await page.ReloadAsync();
        await Expect(page.Locator("p[role='status'] > strong")).ToHaveTextAsync("Active");
        await AssertTimesAsync();

        async Task AssertTimesAsync()
        {
            var eventTimes = page.Locator("main p").Filter(new() { HasText = $"Event time ({EventZone})" }).Locator("time");
            await Expect(eventTimes).ToHaveCountAsync(2);
            await Expect(eventTimes.Nth(0)).ToHaveTextAsync($"{date} 20:30 {eventOffset}");
            await Expect(eventTimes.Nth(1)).ToHaveTextAsync($"{date} 21:30 {eventOffset}");
            await Expect(eventTimes.Nth(0)).ToHaveAttributeAsync("datetime", startUtc.ToString("O", CultureInfo.InvariantCulture));
            await Expect(eventTimes.Nth(1)).ToHaveAttributeAsync("datetime", endUtc.ToString("O", CultureInfo.InvariantCulture));
            await Expect(page.Locator("main p").Filter(new() { HasText = "Your device time (Europe/Prague):" }))
                .ToHaveTextAsync($"Your device time (Europe/Prague): {nextDate} 02:30 {deviceOffset} \u2013 {nextDate} 03:30 {deviceOffset}");
        }
    }

    private static async Task AssertInheritedZoneAsync(IPage page)
    {
        await Expect(page.GetByText($"Event time zone: {EventZone} (inherited, read-only).", new() { Exact = true })).ToBeVisibleAsync();
        var zoneLabel = new Regex("time zone", RegexOptions.IgnoreCase);
        await Expect(page.GetByRole(AriaRole.Textbox, new() { NameRegex = zoneLabel })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Combobox, new() { NameRegex = zoneLabel })).ToHaveCountAsync(0);
    }
}
