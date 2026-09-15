using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Prepared real-host acceptance for framework reconnect and HTTP authentication boundaries; execution remains restricted to the parent-managed CI browser.</summary>
/// <param name="fixture">Existing loopback-only browser fixture, with its origin guard unchanged.</param>
public sealed class HostLifecycleBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
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
        await signingIn.Locator("#persona").SelectOptionAsync("Bob");
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.RouteAsync("**/auth/complete?**", async route =>
        {
            var response = await route.FetchAsync(new() { MaxRedirects = 0 });
            try
            {
                Assert.Equal(200, response.Status);
                captured.TrySetResult();
                await release.Task;
                await route.FulfillAsync(new() { Response = response });
            }
            finally { await response.DisposeAsync(); }
        });
        var submit = signingIn.GetByRole(AriaRole.Button, new() { Name = "Sign in with synthetic identity", Exact = true })
            .ClickAsync();
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await old.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).ClickAsync();
            await Expect(old.GetByRole(AriaRole.Link, new() { Name = "View sign-in options", Exact = true })).ToBeVisibleAsync();
        }
        finally
        {
            release.TrySetResult();
            await submit;
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
