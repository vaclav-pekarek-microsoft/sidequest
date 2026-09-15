using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components.Pages.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies completion reads an actual protected cookie ticket, never a spoofed URL generation or a failed sign-in result.</summary>
public sealed class ExperienceAuthenticationTests : BunitContext
{
    /// <summary>Creates isolated ephemeral cookie protection so tests never use deployment keys, providers or persisted credentials.</summary>
    public ExperienceAuthenticationTests()
    {
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddAuthentication(FoundationAuthenticationSettings.CookieScheme)
            .AddCookie(FoundationAuthenticationSettings.CookieScheme);
    }

    /// <summary>Only a successful protected cookie with the initiating device generation renders a completion marker; anonymous and tampered cookies cannot substitute query data.</summary>
    /// <param name="kind">Valid, tampered, or absent cookie input.</param>
    /// <returns>Completion after real cookie issuance/authentication and rendered boundary assertions.</returns>
    [Theory]
    [InlineData("valid")]
    [InlineData("tampered")]
    [InlineData("absent")]
    public async Task CompletionUsesProtectedCookieGenerationNotQuery(string kind)
    {
        const string epoch = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        string cookie;
        await using (var issuance = Services.CreateAsyncScope())
        {
            var context = new DefaultHttpContext { RequestServices = issuance.ServiceProvider };
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "synthetic")], "test"));
            await context.SignInAsync(FoundationAuthenticationSettings.CookieScheme, principal,
                ExperienceAuthentication.CreateProperties("/quests", epoch));
            cookie = Assert.IsType<string>(Assert.Single(context.Response.Headers.SetCookie)).Split(';')[0];
        }
        await using var request = Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = request.ServiceProvider };
        if (kind != "absent") http.Request.Headers.Cookie = kind == "valid" ? cookie : cookie + "tampered";
        http.Request.QueryString = new QueryString("?experienceEpoch=bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
        var result = await http.AuthenticateAsync(FoundationAuthenticationSettings.CookieScheme);
        Assert.Equal(kind == "valid", result.Succeeded);
        var component = Render<CascadingValue<HttpContext>>(p => p.Add(c => c.Value, http).AddChildContent<AuthenticationComplete>());
        Assert.Equal(kind == "valid" ? epoch : "", component.Find("[data-authentication-completion]")
            .GetAttribute("data-authentication-completion"));
        Assert.DoesNotContain("bbbbbbbb-bbbb", component.Markup);
        Assert.Empty(component.FindAll("[data-connection]"));
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
