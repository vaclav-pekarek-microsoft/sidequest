using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Verifies the shared sign-in entry opens the static sign-in page without prematurely changing authentication state.</summary>
public sealed class SignInLinkTests : BunitContext
{
    /// <summary>Both deployed magic-code and synthetic modes open the static form before any authentication-change handshake.</summary>
    /// <param name="synthetic">Whether isolated development personas replace deployed magic-code authentication.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignInStartsTheConfiguredFlowWithTheCorrectDeviceBoundary(bool synthetic)
    {
        Services.AddSingleton(new FoundationAuthenticationSettings(
            synthetic, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null));

        var component = Render<SignInLink>(parameters => parameters.Add(link => link.Class, "button-link"));
        var link = component.Find("a");

        Assert.Equal("/signin", link.GetAttribute("href"));
        Assert.False(link.HasAttribute("data-authentication-change"));
        Assert.Equal("false", link.GetAttribute("data-enhance-nav"));
        Assert.Equal("button-link", link.GetAttribute("class"));
        Assert.Equal("Sign in", link.TextContent);
    }
}
