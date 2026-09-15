using Microsoft.Playwright;
using Sidequest.BrowserTests.FoundationBrowser;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.SecondaryExperience;

/// <summary>Prepared parent-CI acceptance of native sign-in before the real App initializer can install its interception boundary.</summary>
/// <param name="fixture">The existing CI-only Chromium fixture with unchanged synthetic origin and browser isolation policies.</param>
public sealed class AuthenticationStartupBrowserTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    /// <summary>A delayed initializer leaves native antiforgery sign-in valid, but empty-generation completion must require an explicit human continuation without activating device saving.</summary>
    /// <returns>Completion after the controlled module barrier, genuine native POST, authenticated manual-completion guidance and explicit UI continuation to an authorized root.</returns>
    [Fact]
    public async Task NativeSignInBeforeInitializerRequiresExplicitMissingGenerationContinuation()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        var intercepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var routeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var moduleRequests = 0;
        var snapshots = 0;
        var rootNavigations = 0;
        page.Request += (_, request) =>
        {
            if (new Uri(request.Url).AbsolutePath == "/experience/joined-snapshot")
                Interlocked.Increment(ref snapshots);
            if (request.IsNavigationRequest && new Uri(request.Url).AbsolutePath == "/")
                Interlocked.Increment(ref rootNavigations);
        };
        const string moduleRoute = "**/Components/App.razor.js";
        async Task HoldFirstInitializerAsync(IRoute route)
        {
            if (Interlocked.Increment(ref moduleRequests) != 1)
            {
                await route.FallbackAsync();
                return;
            }
            intercepted.TrySetResult();
            try
            {
                await release.Task;
                // Preserve the fixture's context-level origin policy, even for the held request.
                await route.FallbackAsync();
                routeCompleted.TrySetResult();
            }
            catch (Exception error)
            {
                routeCompleted.TrySetException(error);
            }
        }
        await context.RouteAsync(moduleRoute, HoldFirstInitializerAsync);
        try
        {
            await page.GotoAsync("/signin", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await intercepted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Expect(page.Locator("[data-connection]")).ToContainTextAsync("Connecting");
            await Expect(page.Locator("select#persona")).ToBeEnabledAsync();
            await page.Locator("select#persona").SelectOptionAsync("Alice");
            await Expect(page.Locator("form[action='/auth/development'] input[name='experienceEpoch']")).ToHaveCountAsync(0);
            var post = await page.RunAndWaitForRequestAsync(
                () => page.GetByRole(AriaRole.Button, new() { Name = "Sign in with synthetic identity", Exact = true }).ClickAsync(),
                request => request.IsNavigationRequest && request.Method == "POST" &&
                    new Uri(request.Url).AbsolutePath == "/auth/development");
            var fields = (post.PostData ?? "").Split('&');
            Assert.True(fields.Any(field => field.StartsWith("__RequestVerificationToken=", StringComparison.Ordinal) &&
                field.Length > "__RequestVerificationToken=".Length), "The native POST must carry its actual antiforgery field.");
            Assert.False(fields.Any(field => field.StartsWith("experienceEpoch=", StringComparison.Ordinal)),
                "The unintercepted native POST must not manufacture an initiating generation.");
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/auth/complete",
                new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var completion = page.Locator("[data-authentication-completion]");
            await Expect(completion).ToHaveAttributeAsync("data-authentication-completion", "");
            await Expect(completion.Locator("[data-authentication-result]")).ToHaveTextAsync(
                "Sign-in succeeded, but device saving could not be activated or this sign-in was superseded. Nothing was refreshed. Continue uses the current online account; saving still requires a successful authorized refresh. If clearing failed, clear this site's data before sharing the device.");
            release.TrySetResult();
            await routeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal("/auth/complete", new Uri(page.Url).AbsolutePath);
            Assert.Equal(0, Volatile.Read(ref snapshots));
            Assert.False(await page.EvaluateAsync<bool>(
                "async () => (await indexedDB.databases()).some(database => database.name === 'sidequest-experience-v1')"));
            await Expect(page.Locator("[data-connection]")).ToHaveCountAsync(0);
            Assert.Equal(0, Volatile.Read(ref rootNavigations));

            var continuation = completion.GetByRole(AriaRole.Link, new() { Name = "Continue to Sidequest", Exact = true });
            await Expect(continuation).ToHaveAttributeAsync("href", "/");
            await Expect(continuation).ToHaveAttributeAsync("data-enhance-nav", "false");
            await continuation.ClickAsync();
            await page.WaitForURLAsync(url => new Uri(url).AbsolutePath == "/",
                new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
            Assert.Equal(1, Volatile.Read(ref rootNavigations));
            await SyntheticSignInSupport.WaitForInterceptorAsync(page);
            await Expect(page.Locator("[data-snapshot]")).ToContainTextAsync("Joined basics saved");
            Assert.True(Volatile.Read(ref snapshots) > 0, "Saving after explicit continuation still requires an authorized HTTP refresh.");
        }
        finally
        {
            release.TrySetResult();
            if (intercepted.Task.IsCompletedSuccessfully)
                await routeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await context.UnrouteAsync(moduleRoute, HoldFirstInitializerAsync);
        }
    }
}
