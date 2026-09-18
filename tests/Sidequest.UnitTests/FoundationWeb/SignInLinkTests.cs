using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Verifies the shared sign-in entry starts Entra directly while retaining the explicitly synthetic persona flow.</summary>
public sealed class SignInLinkTests : BunitContext
{
    /// <summary>Entra navigation participates in device clearing and performs a native challenge; synthetic mode still opens its persona picker without prematurely clearing the device.</summary>
    /// <param name="synthetic">Whether isolated development personas replace real Entra admission.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignInStartsTheConfiguredFlowWithTheCorrectDeviceBoundary(bool synthetic)
    {
        Services.AddSingleton(new FoundationAuthenticationSettings(
            synthetic, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null));

        var component = Render<SignInLink>(parameters => parameters.Add(link => link.Class, "button-link"));
        var link = component.Find("a");

        Assert.Equal(synthetic ? "/signin" : "/auth/login", link.GetAttribute("href"));
        Assert.Equal(!synthetic, link.HasAttribute("data-authentication-change"));
        Assert.Equal("false", link.GetAttribute("data-enhance-nav"));
        Assert.Equal("button-link", link.GetAttribute("class"));
        Assert.Equal("Sign in", link.TextContent);
    }
}
