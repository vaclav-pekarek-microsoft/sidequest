using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.CoreBrowser;

/// <summary>Exercises composed Event and Quest workflows through the real synthetic-auth application in isolated CI browser sessions.</summary>
/// <param name="fixture">CI-only Chromium with off-origin requests blocked and an isolated, migrated application database.</param>
public sealed class CoreWorkflowBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    /// <summary>Proves membership consent, persisted public Quest creation, exclusive participation and recipient-only HTTP calendar recovery.</summary>
    /// <returns>A task completing after the independently authenticated owner and member finish the workflow.</returns>
    [Fact]
    public async Task MembershipApprovalPublicQuestParticipationAndCalendarRecovery()
    {
        await using var ownerContext = await fixture.CreateContextAsync();
        await using var memberContext = await fixture.CreateContextAsync();
        var owner = await SignedInAsync(ownerContext, "Alice");
        var member = await SignedInAsync(memberContext, "Bob");
        var eventId = await CreateEventAsync(owner);
        await BecomeMemberAsync(owner, member, eventId, "Bob");
        var questId = await CreateQuestAsync(owner, eventId);
        await Expect(owner.GetByRole(AriaRole.Heading, new() { Name = "Your participation: None", Exact = true })).ToBeVisibleAsync();

        await member.GotoAsync($"/quests/{questId}");
        await member.GetByRole(AriaRole.Button, new() { Name = "Follow", Exact = true }).ClickAsync();
        await ParticipationAsync(member, "Following");
        await member.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true }).ClickAsync();
        await ParticipationAsync(member, "Joined");
        await member.ReloadAsync();
        await ParticipationAsync(member, "Joined");
        await Expect(member.GetByRole(AriaRole.Button, new() { Name = "Unfollow", Exact = true })).ToHaveCountAsync(0);

        var download = await member.RunAndWaitForDownloadAsync(() =>
            member.GetByRole(AriaRole.Link, new() { Name = "Download my calendar entry", Exact = true }).ClickAsync());
        Assert.Equal("sidequest.ics", download.SuggestedFilename);
        var path = await download.PathAsync();
        Assert.NotNull(path);
        var calendar = await File.ReadAllTextAsync(path);
        Assert.Contains("METHOD:REQUEST", calendar, StringComparison.Ordinal);
        Assert.Contains($"UID:{questId:N}@sidequest.calendar", calendar, StringComparison.Ordinal);
        var attendee = Assert.Single(calendar.Split("\r\n"), line => line.StartsWith("ATTENDEE", StringComparison.Ordinal));
        Assert.Contains("mailto:bob@sample.invalid", attendee, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice@sample.invalid", calendar, StringComparison.OrdinalIgnoreCase);

        await member.GetByRole(AriaRole.Button, new() { Name = "Leave Quest", Exact = true }).ClickAsync();
        await ParticipationAsync(member, "None");
        await member.ReloadAsync();
        await ParticipationAsync(member, "None");
        await Expect(member.GetByRole(AriaRole.Link, new() { Name = "Download my calendar entry", Exact = true })).ToHaveCountAsync(0);
        await Expect(member.GetByRole(AriaRole.Button, new() { Name = "Follow", Exact = true })).ToBeVisibleAsync();
    }

    /// <summary>Proves private invitation grants immediate access, ordinary Event ownership grants none, moderation hides rosters, and revocation removes access.</summary>
    /// <returns>A task completing after three separate identities traverse their distinct authorization paths.</returns>
    [Fact]
    public async Task PrivateInvitationModerationAndRevocationPreserveDistinctAccess()
    {
        await using var eventOwnerContext = await fixture.CreateContextAsync();
        await using var questOwnerContext = await fixture.CreateContextAsync();
        await using var inviteeContext = await fixture.CreateContextAsync();
        var eventOwner = await SignedInAsync(eventOwnerContext, "Alice");
        var questOwner = await SignedInAsync(questOwnerContext, "Bob");
        var invitee = await SignedInAsync(inviteeContext, "Carol");
        var eventId = await CreateEventAsync(eventOwner);
        await BecomeMemberAsync(eventOwner, questOwner, eventId, "Bob");
        await BecomeMemberAsync(eventOwner, invitee, eventId, "Carol");
        var questId = await CreateQuestAsync(questOwner, eventId, isPrivate: true);
        var title = await questOwner.GetByRole(AriaRole.Heading, new() { Level = 1 }).InnerTextAsync();

        await AssertPrivateUnavailableAsync(invitee, questId, title);
        await AssertPrivateUnavailableAsync(eventOwner, questId, title);
        await eventOwner.GotoAsync($"/events/{eventId}");
        await eventOwner.GetByRole(AriaRole.Link, new() { Name = "Quest moderation", Exact = true }).ClickAsync();
        await Expect(eventOwner).ToHaveURLAsync(new Regex("/quests\\?view=Moderation&eventId="));
        await eventOwner.GetByRole(AriaRole.Link, new() { Name = title, Exact = true }).ClickAsync();
        await Expect(eventOwner.GetByRole(AriaRole.Heading, new() { Name = "Event-owner moderation", Exact = true })).ToBeVisibleAsync();
        await Expect(eventOwner.GetByRole(AriaRole.Heading, new() { Name = "Attendees", Exact = true })).ToHaveCountAsync(0);
        await Expect(eventOwner.GetByRole(AriaRole.Heading, new() { Name = "Active private invitations (immediate access)", Exact = true })).ToHaveCountAsync(0);
        await Expect(eventOwner.GetByRole(AriaRole.Link, new() { Name = "Edit content", Exact = true })).ToHaveCountAsync(0);

        await ChooseMemberAsync(questOwner, "Carol");
        await ConfirmQuestActionAsync(questOwner, "Invite (immediate access)");
        await invitee.GotoAsync($"/quests/{questId}");
        await Expect(invitee.GetByRole(AriaRole.Heading, new() { Name = title, Exact = true })).ToBeVisibleAsync();
        await ParticipationAsync(invitee, "None");
        await invitee.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true }).ClickAsync();
        await ParticipationAsync(invitee, "Joined");

        await questOwner.ReloadAsync();
        await ChooseMemberAsync(questOwner, "Carol");
        await ConfirmQuestActionAsync(questOwner, "Revoke invitation");
        await AssertPrivateUnavailableAsync(invitee, questId, title);
        var calendar = await inviteeContext.APIRequest.GetAsync($"/notifications/calendar/{questId}");
        Assert.Equal(404, calendar.Status);
        Assert.DoesNotContain(title, await calendar.TextAsync(), StringComparison.Ordinal);
    }

    /// <summary>Proves a stale Event editor cannot overwrite a committed winner and retains unsaved form input for explicit recovery.</summary>
    /// <returns>A task completing after both editor contexts and a persisted reload are inspected.</returns>
    [Fact]
    public async Task ConcurrentEventEditorsPreserveWinnerAndUnsavedConflictInput()
    {
        await using var firstContext = await fixture.CreateContextAsync();
        await using var secondContext = await fixture.CreateContextAsync();
        var first = await SignedInAsync(firstContext, "Alice");
        var second = await SignedInAsync(secondContext, "Alice");
        var eventId = await CreateEventAsync(first);
        await first.GotoAsync($"/events/{eventId}/edit");
        await second.GotoAsync($"/events/{eventId}/edit");
        var winner = $"Winner {Guid.NewGuid():N}";
        var unsaved = $"Unsaved {Guid.NewGuid():N}";
        var firstName = NameInput(first);
        var secondName = NameInput(second);
        await Expect(firstName).ToBeEditableAsync();
        await Expect(secondName).ToBeEditableAsync();
        await firstName.FillAsync(winner);
        await first.GetByRole(AriaRole.Button, new() { Name = "Save Draft or changes", Exact = true }).ClickAsync();
        await Expect(first).ToHaveURLAsync(fixture.Settings.At($"/events/{eventId}").AbsoluteUri);
        await secondName.FillAsync(unsaved);
        await second.GetByRole(AriaRole.Button, new() { Name = "Save Draft or changes", Exact = true }).ClickAsync();
        await Expect(second.GetByRole(AriaRole.Alert)).ToHaveTextAsync(
            "This action is no longer allowed or the item changed. Reload and review the current state before retrying.");
        await Expect(secondName).ToHaveValueAsync(unsaved);
        await first.ReloadAsync();
        await Expect(first.GetByRole(AriaRole.Link, new() { Name = winner, Exact = true })).ToBeVisibleAsync();
        await Expect(first.GetByText(unsaved, new() { Exact = true })).ToHaveCountAsync(0);
    }

    /// <summary>Checks keyboard-reachable real participation and page layout at a 360 CSS-pixel viewport.</summary>
    /// <returns>A task completing after Event/Quest layouts and a keyboard join are verified.</returns>
    [Fact]
    public async Task Mobile360RealQuestJoinIsKeyboardReachableWithoutOverflow()
    {
        await using var context = await fixture.CreateContextAsync(360);
        var page = await SignedInAsync(context, "Alice");
        var eventId = await CreateEventAsync(page);
        await NoOverflowAsync(page);
        await CreateQuestAsync(page, eventId);
        await NoOverflowAsync(page);
        var join = page.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true });
        await TabToAsync(page, join);
        await page.Keyboard.PressAsync("Enter");
        await ParticipationAsync(page, "Joined");
        await NoOverflowAsync(page);
    }

    /// <summary>Exercises the real upload circuit, image decoder and explicit unconfigured-provider failure without discarding unsaved text.</summary>
    /// <returns>A task completing after keyboard-reachable mobile upload, truthful failure and successful ordinary text persistence.</returns>
    [Fact]
    public async Task MobileCoverUpload_UnconfiguredProviderPreservesTextAndDoesNotReportSuccess()
    {
        await using var context = await fixture.CreateContextAsync(360);
        var page = await SignedInAsync(context, "Alice");
        var eventId = await CreateEventAsync(page);
        var questId = await CreateQuestAsync(page, eventId);
        await page.GetByRole(AriaRole.Link, new() { Name = "Edit content", Exact = true }).ClickAsync();
        var title = page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true });
        await Expect(title).ToBeEditableAsync();
        var unsaved = $"Cover failure preserves {Guid.NewGuid():N}";
        await title.FillAsync(unsaved);
        var upload = page.GetByLabel("Upload cover", new() { Exact = true });
        await Expect(upload).ToBeEnabledAsync();
        await TabToAsync(page, upload);
        await NoOverflowAsync(page);
        await upload.SetInputFilesAsync(new FilePayload
        {
            Name = "synthetic-cover.png",
            MimeType = "image/png",
            Buffer = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY2BIYfgPAAIwAWT+qwLNAAAAAElFTkSuQmCC")
        });
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Private media configuration is missing or invalid.");
        await Expect(title).ToHaveValueAsync(unsaved);
        await Expect(page.GetByText("Cover updated.", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(page.Locator(".cover-editor img[src^='media/covers/']")).ToHaveCountAsync(0);
        await NoOverflowAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(fixture.Settings.At($"/quests/{questId}").AbsoluteUri);
        await page.ReloadAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = unsaved, Exact = true })).ToBeVisibleAsync();
    }

    /// <summary>Verifies real administration navigation and loaded controls without changing shared settings, while ordinary workforce users cannot read protected forms or assignments.</summary>
    /// <returns>A task completing after an administrator visits all four screens and a nonadministrator is denied on each route.</returns>
    [Fact]
    public async Task AdministrationNavigationLoadsRealServicesAndDeniesOrdinaryWorkforce()
    {
        await using var administratorContext = await fixture.CreateContextAsync();
        await using var memberContext = await fixture.CreateContextAsync();
        var administrator = await SignedInAsync(administratorContext, "Admin");
        var member = await SignedInAsync(memberContext, "Alice");
        await administrator.GetByRole(AriaRole.Link, new() { Name = "Administration", Exact = true }).ClickAsync();
        await Expect(administrator.GetByRole(AriaRole.Heading, new() { Name = "Administrators", Exact = true })).ToBeVisibleAsync();
        await Expect(administrator.GetByLabel("Search directory-maintained display names (2–100 characters)", new() { Exact = true })).ToBeEditableAsync();
        await Expect(administrator.GetByText("Admin — Eligible administrator", new() { Exact = false })).ToBeVisibleAsync();

        var navigation = administrator.GetByRole(AriaRole.Navigation, new() { Name = "Administration", Exact = true });
        await navigation.GetByRole(AriaRole.Link, new() { Name = "Business email", Exact = true }).ClickAsync();
        await Expect(administrator.GetByLabel("Message brand (1–80 characters)", new() { Exact = true })).ToHaveValueAsync("Sidequest");
        await navigation.GetByRole(AriaRole.Link, new() { Name = "Email templates", Exact = true }).ClickAsync();
        await Expect(administrator.GetByLabel("Subject (1–200 single-line characters)", new() { Exact = true })).ToBeEditableAsync();
        await navigation.GetByRole(AriaRole.Link, new() { Name = "Ownership recovery", Exact = true }).ClickAsync();
        await Expect(administrator.GetByLabel("Resource ID", new() { Exact = true })).ToBeEditableAsync();
        await Expect(administrator.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);

        foreach (var route in new[] { "/administration", "/administration/email", "/administration/templates", "/administration/recovery" })
        {
            await member.GotoAsync(route);
            await Expect(member.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Administrator access is required.");
            await Expect(member.Locator("#admin-search, #email-brand, #template-subject, #recovery-id")).ToHaveCountAsync(0);
            await Expect(member.GetByText("Admin — Eligible administrator", new() { Exact = false })).ToHaveCountAsync(0);
        }
    }

    /// <summary>Checks that anonymous access to every core and administration entry surface is challenged before protected UI is shown.</summary>
    /// <param name="route">A protected Event, Quest, notification or administration entry route.</param>
    /// <returns>A task completing after sign-in redirection and absent product actions are checked.</returns>
    [Theory]
    [InlineData("/events")]
    [InlineData("/events/create")]
    [InlineData("/quests")]
    [InlineData("/quests/create")]
    [InlineData("/notifications")]
    [InlineData("/notifications/preferences")]
    [InlineData("/administration")]
    [InlineData("/administration/email")]
    [InlineData("/administration/templates")]
    [InlineData("/administration/recovery")]
    public async Task AnonymousCoreRoutesRequireSignIn(string route)
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(route);
        await Expect(page).ToHaveURLAsync(new Regex("/signin(?:\\?.*)?$"));
        await Expect(page.Locator("select#persona")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Publish Event", Exact = true })).ToHaveCountAsync(0);
    }

    private async Task<IPage> SignedInAsync(IBrowserContext context, string persona)
    {
        var page = await context.NewPageAsync();
        await Sidequest.BrowserTests.SecondaryExperience.SyntheticLoginDiagnostics.ObserveAsync(
            page, fixture.Settings, "Core", async () =>
            {
                await page.GotoAsync("/signin");
                var select = page.Locator("select#persona");
                await Expect(select).ToBeVisibleAsync();
                var option = select.GetByRole(AriaRole.Option, new() { NameRegex = new($"^{Regex.Escape(persona)}\\b") });
                var value = await option.GetAttributeAsync("value");
                Assert.False(string.IsNullOrEmpty(value));
                await select.SelectOptionAsync(value!);
                await Sidequest.BrowserTests.SecondaryExperience.SyntheticSignInSupport.WaitForInterceptorAsync(page);
                await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with synthetic identity", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
            });
        return page;
    }

    private async Task<Guid> CreateEventAsync(IPage page)
    {
        await page.GotoAsync("/events/create");
        await Expect(page.Locator("[data-connection]")).ToHaveTextAsync("Connected — actions still require current server authorization.");
        try
        {
            await Expect(NameInput(page)).ToBeEditableAsync();
        }
        catch (PlaywrightException)
        {
            Console.WriteLine(await page.EvaluateAsync<string>("""
                () => JSON.stringify({
                  connection: document.querySelector('[data-connection]')?.textContent,
                  fieldsets: [...document.querySelectorAll('fieldset')].map(x => x.disabled),
                  fields: [...document.querySelectorAll('fluent-text-field')].map(x => ({
                    disabled: x.hasAttribute('disabled'),
                    nativeDisabled: x.shadowRoot?.querySelector('input')?.disabled
                  }))
                })
                """));
            throw;
        }
        await NameInput(page).FillAsync($"Journey {Guid.NewGuid():N}");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Discovery summary (visible to eligible users)", Exact = true }).FillAsync("A synthetic browser acceptance Event.");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Description (members only; plain text)", Exact = true }).FillAsync("Member-only browser acceptance description.");
        var date = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await page.GetByLabel("Start date, inclusive", new() { Exact = true }).FillAsync(date);
        await page.GetByLabel("End date, inclusive", new() { Exact = true }).FillAsync(date);
        await page.GetByRole(AriaRole.Button, new() { Name = "Save Draft or changes", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/events/[0-9a-f-]{36}$"));
        var id = Guid.Parse(new Uri(page.Url).Segments[^1]);
        await page.GetByRole(AriaRole.Button, new() { Name = "Publish Event", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Create Quest", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveCountAsync(0);
        return id;
    }

    private static async Task BecomeMemberAsync(IPage owner, IPage member, Guid eventId, string persona)
    {
        await member.GotoAsync($"/events/{eventId}");
        await Expect(member.GetByText("Member-only browser acceptance description.", new() { Exact = true })).ToHaveCountAsync(0);
        await member.GetByRole(AriaRole.Button, new() { Name = "Request membership", Exact = true }).ClickAsync();
        await Expect(member.GetByText("Your request is pending.", new() { Exact = false })).ToBeVisibleAsync();
        await owner.GotoAsync($"/events/{eventId}/requests");
        var request = owner.Locator("article").Filter(new() { HasTextRegex = new($"\\b{Regex.Escape(persona)}\\b") });
        await request.GetByRole(AriaRole.Button, new() { Name = "Approve membership", Exact = true }).ClickAsync();
        await Expect(request.GetByText(new Regex("\\bApproved\\b"))).ToBeVisibleAsync();
        await member.ReloadAsync();
        await Expect(member.GetByText("Member-only browser acceptance description.", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task<Guid> CreateQuestAsync(IPage page, Guid eventId, bool isPrivate = false)
    {
        await page.GotoAsync($"/quests/create?eventId={eventId}");
        var title = page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true });
        await Expect(title).ToBeEditableAsync();
        await title.FillAsync($"Activity {Guid.NewGuid():N}");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Description (plain text)", Exact = true }).FillAsync("Synthetic private-safe activity description.");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Location (required to publish)", Exact = true }).FillAsync("Test meeting point");
        await page.GetByRole(AriaRole.Combobox, new() { NameRegex = new("^Visibility\\b") }).SelectOptionAsync(isPrivate ? "Private" : "Public");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/quests/[0-9a-f-]{36}$"));
        var id = Guid.Parse(new Uri(page.Url).Segments[^1]);
        await ConfirmQuestActionAsync(page, "Publish draft");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true })).ToBeVisibleAsync();
        return id;
    }

    private static async Task ChooseMemberAsync(IPage page, string persona)
    {
        var select = page.GetByRole(AriaRole.Combobox, new() { NameRegex = new("^Event member\\b") });
        var option = select.GetByRole(AriaRole.Option, new() { NameRegex = new($"^{Regex.Escape(persona)}\\b") });
        await Expect(option).ToHaveCountAsync(1);
        var value = await option.GetAttributeAsync("value");
        Assert.False(string.IsNullOrEmpty(value));
        await select.SelectOptionAsync(value!);
    }

    private static async Task ConfirmQuestActionAsync(IPage page, string action)
    {
        var reason = page.GetByRole(AriaRole.Textbox, new() { NameRegex = new("^Reason \\(") });
        await reason.FillAsync("Browser acceptance action.");
        await page.GetByRole(AriaRole.Checkbox, new() { NameRegex = new("^I confirm this action") }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = action, Exact = true }).ClickAsync();
        await Expect(reason).ToHaveValueAsync("");
        await Expect(page.GetByText("Change saved. Required delivery will be attempted durably.", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static Task ParticipationAsync(IPage page, string value) =>
        Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Your participation: {value}", Exact = true })).ToBeVisibleAsync();

    private static ILocator NameInput(IPage page) =>
        page.GetByRole(AriaRole.Textbox, new() { NameRegex = new("^Name \\(3") });

    private static async Task AssertPrivateUnavailableAsync(IPage page, Guid questId, string title)
    {
        await page.GotoAsync($"/quests/{questId}");
        await Expect(page.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Exact = true })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true })).ToHaveCountAsync(0);
    }

    private static async Task TabToAsync(IPage page, ILocator target)
    {
        await Expect(target).ToBeVisibleAsync();
        for (var presses = 0; presses < 48; presses++)
        {
            if (await target.EvaluateAsync<bool>("element => element.matches(':focus-within')"))
                return;
            await page.Keyboard.PressAsync("Tab");
        }
        Assert.Fail("The product action was not keyboard reachable within a bounded page traversal.");
    }

    private static async Task NoOverflowAsync(IPage page)
    {
        Assert.Equal(360, await page.EvaluateAsync<int>("window.innerWidth"));
        var overflows = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth || document.body.scrollWidth > document.documentElement.clientWidth");
        var diagnostic = overflows
            ? await page.EvaluateAsync<string>("""
                () => JSON.stringify(Array.from(document.querySelectorAll('main *'))
                    .filter(element => element.getBoundingClientRect().right > document.documentElement.clientWidth)
                    .slice(0, 12).map(element => ({tag: element.tagName, width: element.getBoundingClientRect().width,
                        text: element.textContent.slice(0, 120)})))
                """)
            : "";
        Assert.False(overflows, $"The composed product page must not scroll horizontally at a 360px viewport. {diagnostic}");
    }
}
