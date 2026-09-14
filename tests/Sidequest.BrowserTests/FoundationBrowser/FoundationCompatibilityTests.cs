using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Sidequest.BrowserTests.FoundationBrowser;

/// <summary>M1 synthetic-auth/Fluent compatibility only, not the A21 release product flows.</summary>
/// <param name="fixture">The CI-only Chromium fixture that supplies isolated contexts and a validated synthetic-app origin.</param>
public sealed class FoundationCompatibilityTests(FoundationBrowserFixture fixture) : IClassFixture<FoundationBrowserFixture>
{
    private const string Completion = "Preview completed. Nothing was saved.";
    private static readonly Regex SyntheticBanner = new("SYNTHETIC DEVELOPMENT");

    /// <summary>Checks each synthetic sign-in path, actual Fluent label binding into the dialog, and its exact no-save completion message.</summary>
    /// <param name="persona">The Alice, Bob, Admin, or Carol option selected through the synthetic login form.</param>
    /// <returns>A task completing after login, dialog assertions, and disposal of the isolated session.</returns>
    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    [InlineData("Admin")]
    [InlineData("Carol")]
    public async Task SyntheticLogin_FluentDialogDisplaysBoundLabel_ReportsNoSave(string persona)
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await LoginAsync(page, persona);
        var label = $"M1 {persona} – Žluťoučký & <preview>";
        var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Preview label", Exact = true });
        await input.FillAsync(label);
        await Expect(input).ToHaveValueAsync(label);
        await page.GetByRole(AriaRole.Button, new() { Name = "Preview dialog", Exact = true }).ClickAsync();
        var dialog = Preview(page);
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(PreviewHost(page).GetByText(label, new() { Exact = true })).ToBeVisibleAsync();
        await PreviewHost(page).GetByRole(AriaRole.Button, new() { Name = "Close preview", Exact = true }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(page.Locator(".foundation-card").GetByRole(AriaRole.Status)).ToHaveTextAsync(Completion);
        await Expect(input).ToHaveValueAsync(label);
        Assert.Equal("/foundation", new Uri(page.Url).AbsolutePath);
    }

    /// <summary>Checks that a new unauthenticated session is redirected to synthetic sign-in without protected preview controls.</summary>
    /// <returns>A task completing after redirect/content assertions and session disposal.</returns>
    [Fact]
    public async Task UnauthenticatedFoundation_RedirectsToSyntheticSignIn()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync("/foundation");
        await Expect(page).ToHaveURLAsync(SignInUrl());
        await Expect(page.Locator("select#persona")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign in with synthetic identity", Exact = true })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Preview label", Exact = true })).ToHaveCountAsync(0);
        await Expect(Preview(page)).ToHaveCountAsync(0);
    }

    /// <summary>Checks keyboard-reachable Fluent preview interaction and absence of horizontal overflow at 360 CSS pixels.</summary>
    /// <returns>A task completing after width assertions before, during, and after the dialog, followed by session disposal.</returns>
    [Fact]
    public async Task Mobile360_KeyboardPreview_HasNoHorizontalOverflow()
    {
        await using var context = await fixture.CreateContextAsync(360);
        var page = await context.NewPageAsync();
        await LoginAsync(page, "Alice");
        await AssertNoOverflowAsync(page);
        var input = page.GetByRole(AriaRole.Textbox, new() { Name = "Preview label", Exact = true });
        await TabToAsync(page, input);
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.TypeAsync("Keyboard-only 360px preview");
        await Expect(input).ToHaveValueAsync("Keyboard-only 360px preview");
        var open = page.GetByRole(AriaRole.Button, new() { Name = "Preview dialog", Exact = true });
        await TabToAsync(page, open);
        await page.Keyboard.PressAsync("Enter");
        var dialog = Preview(page);
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(PreviewHost(page).GetByText("Keyboard-only 360px preview", new() { Exact = true })).ToBeVisibleAsync();
        await AssertNoOverflowAsync(page);
        await TabToAsync(page, PreviewHost(page).GetByRole(AriaRole.Button, new() { Name = "Close preview", Exact = true }));
        await page.Keyboard.PressAsync("Enter");
        await Expect(dialog).ToBeHiddenAsync();
        await Expect(page.Locator(".foundation-card").GetByRole(AriaRole.Status)).ToHaveTextAsync(Completion);
        await AssertNoOverflowAsync(page);
    }

    /// <summary>Checks POST logout and denial of a subsequent protected navigation using the same browser session.</summary>
    /// <returns>A task completing after the outgoing method, redirect, and absent protected-control assertions.</returns>
    [Fact]
    public async Task Logout_PostsAndRevokesProtectedPageAccess()
    {
        await using var context = await fixture.CreateContextAsync();
        var page = await context.NewPageAsync();
        await LoginAsync(page, "Bob");
        var signOut = page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true });
        var request = await page.RunAndWaitForRequestAsync(
            () => signOut.ClickAsync(),
            request => request.IsNavigationRequest && request.Method == "POST");
        Assert.True(fixture.Settings.IsSameOrigin(request.Url));
        Assert.Equal("POST", request.Method);
        await Expect(signOut).ToHaveCountAsync(0);
        // A new protected navigation with the same browser cookies must require login again.
        await page.GotoAsync("/foundation");
        await Expect(page).ToHaveURLAsync(SignInUrl());
        await Expect(page.Locator("select#persona")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Preview label", Exact = true })).ToHaveCountAsync(0);
    }

    private async Task LoginAsync(IPage page, string persona)
    {
        await page.GotoAsync("/signin");
        await Expect(page).ToHaveURLAsync(SignInUrl());
        var select = page.Locator("select#persona");
        await Expect(select).ToBeVisibleAsync();
        var option = select.GetByRole(AriaRole.Option, new() { NameRegex = new($"^{Regex.Escape(persona)}\\b") });
        await Expect(option).ToHaveCountAsync(1);
        var value = await option.GetAttributeAsync("value");
        Assert.False(string.IsNullOrEmpty(value));
        await select.SelectOptionAsync(value!);
        await Expect(select).ToHaveValueAsync(value!);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in with synthetic identity", Exact = true }).ClickAsync();
        // Wait for the login POST/navigation to finish before issuing another navigation.
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
        await page.GotoAsync("/foundation");
        await Expect(page).ToHaveURLAsync(fixture.Settings.At("/foundation").AbsoluteUri);
        await Expect(page.GetByText(SyntheticBanner)).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Preview label", Exact = true })).ToBeEditableAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true })).ToBeVisibleAsync();
    }

    private Regex SignInUrl() => new($"^{Regex.Escape(fixture.Settings.At("/signin").AbsoluteUri)}(?:\\?.*)?$");

    private static ILocator Preview(IPage page) =>
        page.GetByRole(AriaRole.Dialog, new() { Name = "Compatibility preview", Exact = true });

    // Fluent's accessible dialog is in shadow DOM; its slotted content belongs to the host.
    private static ILocator PreviewHost(IPage page) =>
        page.Locator("fluent-dialog").Filter(new() { Has = Preview(page) });

    private static async Task TabToAsync(IPage page, ILocator target)
    {
        await Expect(target).ToBeVisibleAsync();
        // Bounded key presses, not sleeps or DOM-forced focus; accommodates shadow-DOM controls.
        for (var presses = 0; presses < 24; presses++)
        {
            if (await target.EvaluateAsync<bool>("element => element.matches(':focus-within')"))
                return;
            await page.Keyboard.PressAsync("Tab");
        }
        Assert.Fail("The target was not keyboard reachable within one bounded foundation-page tab traversal.");
    }

    private static async Task AssertNoOverflowAsync(IPage page)
    {
        var width = await page.EvaluateAsync<int>("window.innerWidth");
        Assert.Equal(360, width);
        var clientWidth = await page.EvaluateAsync<int>("document.documentElement.clientWidth");
        // A vertical scrollbar may consume some viewport width; it is not horizontal overflow.
        Assert.InRange(clientWidth, 1, 360);
        var overflow = await page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth || document.body.scrollWidth > document.documentElement.clientWidth");
        Assert.False(overflow, "The foundation page must not scroll horizontally at a 360px viewport.");
    }
}
