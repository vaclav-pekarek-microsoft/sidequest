using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Exercises real synthetic-auth dashboard, installation, full Joined refresh and offline cold launch after parent composition.</summary>
/// <param name="fixture">Existing CI-only browser fixture with an explicitly scoped service-worker opt-in.</param>
public sealed class ExperienceJourneyBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    /// <summary>A newly opened offline page renders joined private basics without a circuit, caches only public assets and exposes no server mutation or details links.</summary>
    /// <returns>Completion after real sign-in/create/join, exact snapshot schema, public cache and cold-launch assertions.</returns>
    [Fact]
    public async Task OfflineColdLaunchRendersJoinedPrivateBasicsWithoutCircuitOrProtectedResponseCache()
    {
        await using var context = await ExperienceBrowserSupport.WorkerContextAsync(fixture);
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        var eventId = await ExperienceBrowserSupport.CreateEventAsync(page);
        var title = $"Private offline {Guid.NewGuid():N}";
        var joined = await ExperienceBrowserSupport.CreateQuestAsync(page, eventId, title, privateQuest: true);
        await page.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your participation: Joined", Exact = true })).ToBeVisibleAsync();
        var owned = await ExperienceBrowserSupport.CreateQuestAsync(page, eventId, $"Owned only {Guid.NewGuid():N}");
        var followed = await ExperienceBrowserSupport.CreateQuestAsync(page, eventId, $"Follow only {Guid.NewGuid():N}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Follow", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your participation: Following", Exact = true })).ToBeVisibleAsync();
        await page.GotoAsync("/");
        await Expect(page.Locator("[data-snapshot]")).ToContainTextAsync("Joined basics saved");
        await page.EvaluateAsync("async()=>await navigator.serviceWorker.ready");
        await page.WaitForFunctionAsync("() => navigator.serviceWorker.controller !== null");
        Guid authorizedAccount;
        var response = await context.APIRequest.GetAsync("/experience/joined-snapshot", new() { MaxRedirects = 0 });
        try
        {
            Assert.Equal(200, response.Status);
            Assert.Equal("no-store", response.Headers["cache-control"]);
            using var wire = JsonDocument.Parse(await response.TextAsync());
            Assert.Equal(new[] { "accountId", "quests", "refreshedUtc" }, wire.RootElement.EnumerateObject().Select(p => p.Name).Order());
            authorizedAccount = wire.RootElement.GetProperty("accountId").GetGuid();
            var records = wire.RootElement.GetProperty("quests").EnumerateArray().ToArray();
            Assert.Contains(records, q => q.GetProperty("id").GetGuid() == joined);
            foreach (var quest in records)
            {
                Assert.Equal(new[] { "endUtc", "eventId", "id", "location", "startUtc", "status", "timeZoneId", "title" },
                    quest.EnumerateObject().Select(p => p.Name).Order());
                Assert.Equal(JsonValueKind.Number, quest.GetProperty("status").ValueKind);
            }
        }
        finally { await response.DisposeAsync(); }
        // JsonElement evaluation adds Playwright reference metadata; inspect the browser's own JSON without rewriting it.
        using var stored = JsonDocument.Parse(await page.EvaluateAsync<string>(
            "async()=>JSON.stringify(await (await import('/experience/snapshot-store.js?v=1')).readSnapshot())"));
        var snapshot = stored.RootElement;
        Assert.Equal(new[] { "accountId", "quests", "refreshedUtc" }, snapshot.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(authorizedAccount, snapshot.GetProperty("accountId").GetGuid());
        var basics = snapshot.GetProperty("quests").EnumerateArray().ToArray();
        Assert.Contains(basics, q => q.GetProperty("id").GetGuid() == joined);
        Assert.DoesNotContain(basics, q => q.GetProperty("id").GetGuid() == owned || q.GetProperty("id").GetGuid() == followed);
        foreach (var quest in basics)
            Assert.Equal(new[] { "endUtc", "eventId", "id", "location", "startUtc", "status", "timeZoneId", "title" },
                quest.EnumerateObject().Select(p => p.Name).Order());
        Assert.DoesNotContain("NEVER-SAVE-THIS-DESCRIPTION", snapshot.ToString());
        var cached = await page.EvaluateAsync<string[]>("""
            async()=> {
                const result=[];
                for(const name of await caches.keys())
                    for(const request of await (await caches.open(name)).keys()) result.push(new URL(request.url).pathname);
                return result;
            }
            """);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "/experience/offline.html", "/experience/offline.css", "/experience/offline.js",
            "/experience/snapshot-store.js", "/experience/refresh.js", "/experience/manifest.webmanifest",
            "/experience/icon.svg", "/experience/icon-192.png", "/experience/icon-512.png"
        };
        Assert.NotEmpty(cached);
        Assert.All(cached, path => Assert.Contains(path, allowed));
        Assert.DoesNotContain("/experience/joined-snapshot", cached);
        Assert.DoesNotContain("/", cached);
        Assert.False(await page.EvaluateAsync<bool>("""
            async title => {
                for(const name of await caches.keys()) {
                    const cache=await caches.open(name);
                    for(const request of await cache.keys()) {
                        const body=await (await cache.match(request)).text();
                        if(body.includes(title)||body.includes('NEVER-SAVE-THIS-DESCRIPTION')) return true;
                    }
                }
                return false;
            }
            """, title));

        await page.CloseAsync();
        await context.SetOfflineAsync(true);
        var cold = await context.NewPageAsync();
        var mutations = new List<string>();
        cold.Request += (_, request) => { if (request.Method != "GET") mutations.Add(request.Method); };
        await cold.GotoAsync("/");
        await Expect(cold.GetByRole(AriaRole.Heading, new() { Name = "Offline joined Quests", Exact = true })).ToBeVisibleAsync();
        await Expect(cold.Locator("#quests h2").Filter(new() { HasText = title })).ToBeVisibleAsync();
        await Expect(cold.Locator("#refresh")).ToContainTextAsync("Offline — last refreshed");
        await Expect(cold.Locator("script[src*='_framework']")).ToHaveCountAsync(0);
        await Expect(cold.Locator("a[href^='/quests/']")).ToHaveCountAsync(0);
        await Expect(cold.GetByRole(AriaRole.Button)).ToHaveCountAsync(1);
        Assert.Equal("Clear saved basics on this device", await cold.GetByRole(AriaRole.Button).InnerTextAsync());
        Assert.Empty(mutations);
        Assert.False(await cold.EvaluateAsync<bool>("()=>document.documentElement.scrollWidth>document.documentElement.clientWidth"));
        await cold.Keyboard.PressAsync("Tab");
        await Expect(cold.GetByRole(AriaRole.Button)).ToBeFocusedAsync();
    }

    /// <summary>Real successful leave and cancellation replace cached state on the next authorized refresh, and cancellation is never presented as actionable Upcoming.</summary>
    /// <returns>Completion after actual mutation, HTTP refresh, and dashboard assertions.</returns>
    [Fact]
    public async Task SuccessfulLeaveAndCancellationReplaceJoinedCacheAndUpcoming()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        var eventId = await ExperienceBrowserSupport.CreateEventAsync(page);
        var quest = await ExperienceBrowserSupport.CreateQuestAsync(page, eventId, $"Refresh {Guid.NewGuid():N}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your participation: Joined", Exact = true })).ToBeVisibleAsync();
        await ExperienceBrowserSupport.RefreshAsync(page);
        Assert.True(await HasQuestAsync(page, quest));
        await page.GetByRole(AriaRole.Button, new() { Name = "Leave Quest", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your participation: None", Exact = true })).ToBeVisibleAsync();
        await ExperienceBrowserSupport.RefreshAsync(page);
        Assert.False(await HasQuestAsync(page, quest));
        await page.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Your participation: Joined", Exact = true })).ToBeVisibleAsync();
        await ExperienceBrowserSupport.ConfirmAsync(page, "Cancel Quest");
        await ExperienceBrowserSupport.RefreshAsync(page);
        var status = await page.EvaluateAsync<int?>("""
            async id => (await (await import('/experience/snapshot-store.js?v=1')).readSnapshot()).quests.find(q=>q.id===id)?.status ?? null
            """, quest.ToString());
        Assert.True(status is null or 4, "Cancellation is absent or explicitly marked Cancelled, never cached as active.");
        await page.GotoAsync("/");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Upcoming Joined", Exact = true })).ToBeVisibleAsync();
        await Expect(page.Locator($"[data-protected-experience] a[href='/quests/{quest}']")).ToHaveCountAsync(0);
    }

    /// <summary>Local sign-out clears the persisted last-account snapshot in another tab; signing in as another persona never restores the previous user's records.</summary>
    /// <returns>Completion after actual antiforgery-protected logout, next sign-in and storage assertions.</returns>
    [Fact]
    public async Task ExplicitLogoutAndAccountSwitchClearPriorDeviceBasicsAcrossTabs()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        await ExperienceBrowserSupport.SaveAsync(page, ExperienceBrowserSupport.Snapshot("PRIOR ACCOUNT"));
        var other = await context.NewPageAsync();
        await other.GotoAsync("/experience/offline.html");
        await Expect(other.Locator("#quests h2")).ToHaveTextAsync("PRIOR ACCOUNT");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "View sign-in options", Exact = true })).ToBeVisibleAsync();
        await Expect(other.Locator("#quests article")).ToHaveCountAsync(0);
        Assert.False(await other.EvaluateAsync<bool>("async()=>!!(await (await import('/experience/snapshot-store.js?v=1')).readSnapshot())"));
        var bob = await ExperienceBrowserSupport.SignInAsync(context, "Bob");
        await bob.GotoAsync("/");
        await Expect(bob.Locator("[data-snapshot]")).ToContainTextAsync("Joined basics saved");
        var snapshot = await bob.EvaluateAsync<string>("async()=>JSON.stringify(await (await import('/experience/snapshot-store.js?v=1')).readSnapshot())");
        Assert.DoesNotContain("PRIOR ACCOUNT", snapshot);
    }

    /// <summary>At 360 CSS pixels, dashboard filters are keyboard reachable; disconnect makes online controls inert without inventing successful reconnection.</summary>
    /// <returns>Completion after layout, keyboard, disconnected controls and install-guidance assertions.</returns>
    [Fact]
    public async Task DashboardInstallAndDisconnectedControlsRemainHonestAt360Pixels()
    {
        await using var context = await fixture.CreateContextAsync(width: 360);
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        await page.GotoAsync("/");
        await Expect(page.Locator("[data-connection]")).ToContainTextAsync("Connected");
        var view = page.GetByRole(AriaRole.Button, new() { Name = "Following", Exact = true });
        await Expect(view).ToBeEnabledAsync();
        await view.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        Assert.False(await page.EvaluateAsync<bool>("()=>document.documentElement.scrollWidth>document.documentElement.clientWidth"));
        await page.EvaluateAsync("async()=> (await import('/Components/Experience/ConnectionStatus.razor.js')).reportCircuitConnection(false)");
        await Expect(page.Locator("[data-connection]")).ToContainTextAsync("Offline or disconnected");
        Assert.True(await page.Locator("fieldset[data-online-actions]").EvaluateAsync<bool>("element=>element.inert"));
        await Expect(page.Locator("[data-refresh]")).ToBeDisabledAsync();
        await page.GotoAsync("/install");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Install Sidequest", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByText("No installation or persistent storage is guaranteed.", new() { Exact = false })).ToBeVisibleAsync();
        Assert.False(await page.EvaluateAsync<bool>("()=>document.documentElement.scrollWidth>document.documentElement.clientWidth"));
    }

    /// <summary>A private invitation without joining is not saved, and revocation by the real owner removes previously joined basics on the next authorized HTTP query.</summary>
    /// <returns>Completion after two real cookie identities, membership approval, invitation, join and revocation.</returns>
    [Fact]
    public async Task PrivateInvitedOnlyNeverLeaksAndOnlineRevocationRemovesJoinedBasics()
    {
        await using var ownerContext = await fixture.CreateContextAsync();
        await using var memberContext = await fixture.CreateContextAsync();
        var owner = await ExperienceBrowserSupport.SignInAsync(ownerContext);
        var member = await ExperienceBrowserSupport.SignInAsync(memberContext, "Bob");
        var eventId = await ExperienceBrowserSupport.CreateEventAsync(owner);
        await member.GotoAsync($"/events/{eventId}");
        await member.GetByRole(AriaRole.Button, new() { Name = "Request membership", Exact = true }).ClickAsync();
        await Expect(member.GetByText("Your request is pending.", new() { Exact = false })).ToBeVisibleAsync();
        await owner.GotoAsync($"/events/{eventId}/requests");
        var request = owner.Locator("article").Filter(new() { HasTextRegex = new("\\bBob\\b") });
        await request.GetByRole(AriaRole.Button, new() { Name = "Approve membership", Exact = true }).ClickAsync();
        await Expect(request.GetByText(new Regex("\\bApproved\\b"))).ToBeVisibleAsync();
        var quest = await ExperienceBrowserSupport.CreateQuestAsync(owner, eventId, $"Revocation {Guid.NewGuid():N}", privateQuest: true);
        var select = owner.GetByRole(AriaRole.Combobox, new() { NameRegex = new("^Event member\\b") });
        var option = select.GetByRole(AriaRole.Option, new() { NameRegex = new("^Bob\\b") });
        var value = await option.GetAttributeAsync("value");
        Assert.NotNull(value);
        await select.SelectOptionAsync(value);
        await ExperienceBrowserSupport.ConfirmAsync(owner, "Invite (immediate access)");
        await member.GotoAsync($"/quests/{quest}");
        await Expect(member.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true })).ToBeVisibleAsync();
        await ExperienceBrowserSupport.RefreshAsync(member);
        Assert.False(await HasQuestAsync(member, quest));
        await member.GetByRole(AriaRole.Button, new() { Name = "Join Quest", Exact = true }).ClickAsync();
        await Expect(member.GetByRole(AriaRole.Heading, new() { Name = "Your participation: Joined", Exact = true })).ToBeVisibleAsync();
        await ExperienceBrowserSupport.RefreshAsync(member);
        Assert.True(await HasQuestAsync(member, quest));
        await select.SelectOptionAsync(value);
        await ExperienceBrowserSupport.ConfirmAsync(owner, "Revoke invitation");
        await member.ReloadAsync();
        await Expect(member.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await ExperienceBrowserSupport.RefreshAsync(member);
        Assert.False(await HasQuestAsync(member, quest));
    }

    private static Task<bool> HasQuestAsync(IPage page, Guid quest) => page.EvaluateAsync<bool>("""
        async id => (await (await import('/experience/snapshot-store.js?v=1')).readSnapshot()).quests.some(q=>q.id===id)
        """, quest.ToString());
}
