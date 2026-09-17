using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components.Pages.Experience;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies completion reads an actual protected cookie ticket, never a spoofed URL generation or a failed sign-in result.</summary>
public sealed class ExperienceAuthenticationTests : BunitContext
{
    /// <summary>Creates isolated ephemeral cookie protection so tests never use deployment keys, providers or persisted credentials.</summary>
    public ExperienceAuthenticationTests()
    {
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(new FoundationAuthenticationSettings(true, DevelopmentPersonas.TenantId,
            DevelopmentPersonas.WorkforceRole, null));
        Services.AddSingleton<ExperienceSessionBinding>();
        Services.AddAuthentication(FoundationAuthenticationSettings.CookieScheme)
            .AddCookie(FoundationAuthenticationSettings.CookieScheme);
    }

    /// <summary>Completion pairs the protected cookie generation with its exact current-session proof; missing generations and legacy sessions cannot enable activation, and query data cannot substitute either value.</summary>
    /// <param name="kind">Valid, generation-free, tampered, absent, or legacy-session cookie input.</param>
    /// <returns>Completion after real cookie issuance/authentication and rendered boundary assertions.</returns>
    [Theory]
    [InlineData("valid")]
    [InlineData("without-generation")]
    [InlineData("tampered")]
    [InlineData("absent")]
    [InlineData("legacy-session")]
    public async Task CompletionUsesProtectedCookieGenerationNotQuery(string kind)
    {
        const string epoch = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        string cookie;
        await using (var issuance = Services.CreateAsyncScope())
        {
            var context = new DefaultHttpContext { RequestServices = issuance.ServiceProvider };
            var principal = DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All.Single(p => p.Name == "Alice"));
            if (kind != "legacy-session")
                WorkforceSession.Stamp(principal, DateTimeOffset.UtcNow);
            await context.SignInAsync(FoundationAuthenticationSettings.CookieScheme, principal,
                ExperienceAuthentication.CreateProperties("/quests", kind == "without-generation" ? null : epoch));
            cookie = Assert.IsType<string>(Assert.Single(context.Response.Headers.SetCookie)).Split(';')[0];
        }
        await using var request = Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = request.ServiceProvider };
        if (kind != "absent") http.Request.Headers.Cookie = kind == "tampered" ? cookie + "tampered" : cookie;
        http.Request.QueryString = new QueryString("?experienceEpoch=bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        var result = await http.AuthenticateAsync(FoundationAuthenticationSettings.CookieScheme);
        Assert.Equal(kind is "valid" or "without-generation" or "legacy-session", result.Succeeded);
        var component = Render<CascadingValue<HttpContext>>(p => p.Add(c => c.Value, http).AddChildContent<AuthenticationComplete>());
        Assert.Equal(kind is "valid" or "legacy-session" ? epoch : "", component.Find("[data-authentication-completion]")
            .GetAttribute("data-authentication-completion"));
        var proof = component.Find("[data-authentication-completion]").GetAttribute("data-authentication-binding");
        var binding = Services.GetRequiredService<ExperienceSessionBinding>();
        if (kind == "valid")
        {
            Assert.False(string.IsNullOrEmpty(proof));
            Assert.NotNull(result.Principal);
            Assert.True(binding.Matches(proof, result.Principal));
            Assert.False(binding.Matches(proof, new ClaimsPrincipal()));
            WorkforceSession.Stamp(result.Principal, DateTimeOffset.UtcNow);
            Assert.False(binding.Matches(proof, result.Principal));
        }
        else
            Assert.Equal("", proof);
        Assert.DoesNotContain("bbbbbbbb-bbbb", component.Markup);
        Assert.Empty(component.FindAll("[data-connection]"));
        if (kind == "without-generation")
        {
            Assert.NotNull(result.Properties);
            Assert.False(result.Properties.Items.ContainsKey(ExperienceAuthentication.EpochProperty));
            var continuation = component.Find("[data-authentication-continue]");
            Assert.Equal("/", continuation.GetAttribute("href"));
            Assert.Equal("false", continuation.GetAttribute("data-enhance-nav"));
            Assert.Equal("Continue to Sidequest", continuation.TextContent);
            Assert.Contains("Checking the device-clearing boundary", component.Find("[data-authentication-result]").TextContent);
        }
    }

    /// <summary>Malformed or missing client generations never become protected completion claims; unsafe return destinations remain local.</summary>
    /// <param name="epoch">Untrusted malformed or absent device metadata.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void InvalidDeviceMetadataDoesNotEnableCompletion(string? epoch)
    {
        var properties = ExperienceAuthentication.CreateProperties("https://outside.invalid", epoch);
        Assert.Equal("/auth/complete?returnUrl=%2F", properties.RedirectUri);
        Assert.False(properties.IsPersistent);
        Assert.False(properties.Items.ContainsKey(ExperienceAuthentication.EpochProperty));
        Assert.Empty(properties.GetTokens());
    }
}
