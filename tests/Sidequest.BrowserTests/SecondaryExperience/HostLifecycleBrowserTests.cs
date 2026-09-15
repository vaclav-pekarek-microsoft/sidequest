using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Prepared real-host acceptance for framework reconnect and HTTP authentication boundaries; execution remains restricted to the parent-managed CI browser.</summary>
/// <param name="fixture">Existing loopback-only browser fixture, with its origin guard unchanged.</param>
public sealed class HostLifecycleBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    /// <summary>An eligible Bob cookie issued through antiforgery-protected HTTP without any client auth signal cannot reactivate an existing Alice circuit after actual reconnect.</summary>
    /// <returns>Completion after different authenticated account IDs, unchanged pre-reconnect DOM, real 409 binding denial, hidden/inert private content and recovery only through a full HTTP reload.</returns>
    [Fact]
    public async Task HttpCookieSwitchWithoutBroadcastKeepsOldCircuitUnavailableAfterReconnect()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        var eventId = await ExperienceBrowserSupport.CreateEventAsync(page);
        var title = $"Alice private binding {Guid.NewGuid():N}";
        var quest = await ExperienceBrowserSupport.CreateQuestAsync(page, eventId, title, privateQuest: true);
        string? circuitProof = null;
        page.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath == "/experience/session" &&
                request.Headers.TryGetValue("x-sidequest-circuit-binding", out var proof))
                circuitProof = proof;
        };
        await page.GotoAsync($"/quests/{quest}");
        await Expect(page.Locator("[data-connection]")).ToHaveTextAsync("Connected — actions still require current server authorization.");
        await Expect(page.Locator("main")).ToContainTextAsync(title);
        Assert.False(await page.Locator("main").EvaluateAsync<bool>("element => element.hidden || element.inert"));
        Assert.False(string.IsNullOrEmpty(circuitProof));
        var oldProof = circuitProof!;
        var alice = await CookieAccountAsync(context);
        var epoch = await page.EvaluateAsync<string?>("async () => (await import('/experience/snapshot-store.js?v=1')).currentEpoch()");

        await SignInCookieAsync(context, page, "Bob");
        var bob = await CookieAccountAsync(context);
        Assert.NotEqual(alice, bob);
        Assert.Equal(epoch, await page.EvaluateAsync<string?>("async () => (await import('/experience/snapshot-store.js?v=1')).currentEpoch()"));
        Assert.False(await page.Locator("main").EvaluateAsync<bool>("element => element.hidden || element.inert"));
        await Expect(page.Locator("main")).ToContainTextAsync(title);

        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        page.Response += (_, response) =>
        {
            if (new Uri(response.Url).AbsolutePath == "/experience/session" && response.Status == 409)
                rejected.TrySetResult();
        };
        await context.SetOfflineAsync(true);
        await Expect(page.Locator("#components-reconnect-modal")).ToHaveClassAsync(
            new Regex("components-reconnect-(show|retrying|failed)"), new() { Timeout = 90_000 });
        Assert.True(await page.Locator("main").EvaluateAsync<bool>("element => element.inert"));
        await context.SetOfflineAsync(false);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(90));
        await Expect(page.Locator("[data-connection]")).ToContainTextAsync("full online reload");
        await Expect(page.Locator("main")).ToBeHiddenAsync();
        Assert.True(await page.Locator("main").EvaluateAsync<bool>("element => element.inert"));
        Assert.Equal(oldProof, circuitProof);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeEnabledAsync();
        var stored = await page.EvaluateAsync<string>("""
            () => new Promise((resolve, reject) => {
                const open = indexedDB.open('sidequest-experience-v1', 1);
                open.onerror = reject;
                open.onsuccess = () => {
                    const db = open.result, tx = db.transaction('state'), read = tx.objectStore('state').get('last-account');
                    read.onsuccess = () => resolve(JSON.stringify(read.result));
                    tx.oncomplete = () => db.close();
                    tx.onerror = reject;
                };
            })
            """);
        Assert.False(stored.Contains(oldProof, StringComparison.Ordinal));
        using (var data = JsonDocument.Parse(stored))
        {
            Assert.Equal(new[] { "blocked", "epoch", "revision", "snapshot" },
                data.RootElement.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(JsonValueKind.Null, data.RootElement.GetProperty("snapshot").ValueKind);
        }
        await page.GotoAsync("/");
        await Expect(page.Locator("[data-connection]")).ToHaveTextAsync("Connected — actions still require current server authorization.");
        await Expect(page.Locator("main")).ToBeVisibleAsync();
        Assert.False(await page.Locator("main").EvaluateAsync<bool>("element => element.inert"));
        await Expect(page.Locator("main")).Not.ToContainTextAsync(title);
        Assert.Equal(bob, await CookieAccountAsync(context));
    }

    private static async Task<string> SignInCookieAsync(IBrowserContext context, IPage page, string persona, string? epoch = null)
    {
        var signInPage = await context.APIRequest.GetAsync("/signin", new() { MaxRedirects = 0 });
        string token;
        try
        {
            Assert.Equal(200, signInPage.Status);
            token = await page.EvaluateAsync<string>("""
                html => new DOMParser().parseFromString(html, 'text/html')
                    .querySelector('form[action="/auth/development"] input[name="__RequestVerificationToken"]').value
                """, await signInPage.TextAsync());
        }
        finally { await signInPage.DisposeAsync(); }
        var form = context.APIRequest.CreateFormData();
        form.Set("__RequestVerificationToken", token);
        form.Set("persona", persona);
        form.Set("returnUrl", "/");
        if (epoch is not null) form.Set("experienceEpoch", epoch);
        var switched = await context.APIRequest.PostAsync("/auth/development", new() { Form = form, MaxRedirects = 0 });
        try
        {
            Assert.Equal(302, switched.Status);
            Assert.StartsWith("/auth/complete", switched.Headers["location"]);
            return switched.Headers["location"];
        }
        finally { await switched.DisposeAsync(); }
    }

    private static async Task<Guid> CookieAccountAsync(IBrowserContext context)
    {
        var response = await context.APIRequest.GetAsync("/experience/joined-snapshot", new() { MaxRedirects = 0 });
        try
        {
            Assert.Equal(200, response.Status);
            using var data = JsonDocument.Parse(await response.TextAsync());
            return data.RootElement.GetProperty("accountId").GetGuid();
        }
        finally { await response.DisposeAsync(); }
    }

    /// <summary>Actual network loss disables online input without losing it; recovery checks current cookies rather than trusting RendererInfo or a missed down callback.</summary>
    /// <returns>Completion after framework events, retained form input, cookie-session requests and global bridge cardinality assertions.</returns>
    [Fact]
    public async Task ActualReconnectKeepsInputAndRechecksCurrentHttpSession()
    {
        await using var context = await fixture.CreateContextAsync(width: 360);
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        var checks = 0;
        page.Request += (_, request) => { if (new Uri(request.Url).AbsolutePath == "/experience/session") Interlocked.Increment(ref checks); };
        await page.GotoAsync("/events/create");
        var name = page.GetByRole(AriaRole.Textbox, new() { NameRegex = new("^Name \\(3") });
        await Expect(name).ToBeEditableAsync();
        await name.FillAsync("Unsaved reconnect input");
        await Expect(page.Locator("[data-connection]")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-connection]")).ToHaveTextAsync("Connected — actions still require current server authorization.");
        var initialChecks = Volatile.Read(ref checks);
        Assert.True(initialChecks > 0);
        await context.SetOfflineAsync(true);
        await Expect(page.Locator("#components-reconnect-modal")).ToHaveClassAsync(
            new Regex("components-reconnect-(show|retrying|failed)"), new() { Timeout = 90_000 });
        Assert.True(await page.Locator("main").EvaluateAsync<bool>("element => element.inert"));
        await Expect(name).ToHaveValueAsync("Unsaved reconnect input");
        await context.SetOfflineAsync(false);
        await Expect(page.Locator("[data-connection]")).ToHaveTextAsync(
            "Connected — actions still require current server authorization.", new() { Timeout = 90_000 });
        await Expect(name).ToBeEditableAsync();
        await Expect(name).ToHaveValueAsync("Unsaved reconnect input");
        Assert.True(Volatile.Read(ref checks) > initialChecks);
        await Expect(page).ToHaveURLAsync(new Regex("/events/create$"));
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth"));
    }

    /// <summary>A real successful second sign-in response delayed until after logout cannot unblock device storage or restore another account's snapshot.</summary>
    /// <returns>Completion after actual cookie issuance, antiforgery logout, delayed completion HTML and atomic-generation assertions.</returns>
    [Fact]
    public async Task DelayedSuccessfulSignInCompletionCannotUndoLaterLogout()
    {
        await using var context = await fixture.CreateContextAsync();
        var old = await ExperienceBrowserSupport.SignInAsync(context);
        await Expect(old.Locator("[data-snapshot]")).ToContainTextAsync("Joined basics saved");
        var signingIn = await context.NewPageAsync();
        await signingIn.GotoAsync("/signin");
        var alice = await CookieAccountAsync(context);
        var epoch = await signingIn.EvaluateAsync<string>(
            "async () => (await import('/Components/Experience/ConnectionStatus.razor.js')).beforeAuthenticationChange()");
        var completionUrl = await SignInCookieAsync(context, signingIn, "Bob", epoch);
        Assert.NotEqual(alice, await CookieAccountAsync(context));
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RouteAsync("**/auth/complete?**", async route =>
        {
            IAPIResponse? response = null;
            try
            {
                response = await route.FetchAsync(new() { MaxRedirects = 0 });
                Assert.Equal(200, response.Status);
                Assert.True((await response.TextAsync()).Contains(
                    $"data-authentication-completion=\"{epoch}\"", StringComparison.Ordinal));
                captured.TrySetResult();
                await release.Task;
                await route.FulfillAsync(new() { Response = response });
            }
            catch (Exception error)
            {
                captured.TrySetException(error);
                throw;
            }
            finally { if (response is not null) await response.DisposeAsync(); }
        });
        // Playwright cannot route a redirect's subsequent URL; navigate to the real protected completion independently.
        var completion = signingIn.GotoAsync(completionUrl);
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // The cookie is now Bob's: obtain his real antiforgery form before the later native logout.
            await old.GotoAsync("/");
            await old.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
            await Expect(old.GetByRole(AriaRole.Link, new() { Name = "View sign-in options", Exact = true })).ToBeVisibleAsync();
        }
        finally
        {
            release.TrySetResult();
            await completion;
        }
        await Expect(signingIn.Locator("[data-authentication-result]")).ToContainTextAsync("superseded");
        Assert.True(await signingIn.EvaluateAsync<bool>("""
            async () => {
                const store = await import('/experience/snapshot-store.js?v=1');
                return await store.currentEpoch() === null && await store.readSnapshot() === null;
            }
            """));
        Assert.Equal(401, await old.EvaluateAsync<int>("async () => (await fetch('/experience/session', {redirect:'manual'})).status"));
    }

    /// <summary>Browser storage policy denial is disclosed on the post-logout page while the actual authentication cookie is still removed.</summary>
    /// <returns>Completion after real sign-out, persistent visible failure guidance and current-session HTTP denial.</returns>
    [Fact]
    public async Task StorageClearFailureIsVisibleButDoesNotPreventRealSignOut()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await ExperienceBrowserSupport.SignInAsync(context);
        await page.EvaluateAsync("""
            () => Object.defineProperty(window, 'indexedDB', {
                configurable: true, get() { throw new DOMException('Synthetic storage policy', 'SecurityError'); }
            })
            """);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/\\?deviceClearFailed=true$"));
        await Expect(page.Locator("[data-authentication-warning]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-authentication-warning]")).ToContainTextAsync("clear this site's data");
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToHaveCountAsync(0);
        Assert.Equal(401, await page.EvaluateAsync<int>("async () => (await fetch('/experience/session', {redirect:'manual'})).status"));
    }
}
