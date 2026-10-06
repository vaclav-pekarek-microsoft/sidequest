using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sidequest.Web.Authentication;

namespace Sidequest.UnitTests.FoundationWeb;

/// <summary>Exercises the deployed cookie authentication composition without an external identity provider.</summary>
public sealed class AuthenticationRegistrationTests
{
    /// <summary>Verifies deployed magic-code sessions use an isolated secure, HTTP-only, non-sliding cookie.</summary>
    /// <returns>A task completing after asynchronous scheme resolution.</returns>
    [Fact]
    public async Task MagicCodeAuthenticationRegistersHardenedApplicationCookie()
    {
        var services = new ServiceCollection();
        var settings = new FoundationAuthenticationSettings(
            false, Guid.NewGuid(), FoundationAuthenticationSettings.MagicCodeRole, null);
        services.AddFoundationAuthentication(settings);
        using var provider = services.BuildServiceProvider();

        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.NotNull(await schemes.GetSchemeAsync(FoundationAuthenticationSettings.CookieScheme));
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(FoundationAuthenticationSettings.CookieScheme);
        Assert.Equal("__Host-Sidequest.Magic", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(TimeSpan.FromHours(1), options.ExpireTimeSpan);
        Assert.False(options.SlidingExpiration);
        Assert.Equal("/signin", options.LoginPath);
    }
}
