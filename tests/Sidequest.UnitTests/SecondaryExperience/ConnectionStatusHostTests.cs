using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components.Experience;
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies the host bridge reauthorizes even when physical disconnection prevented the down callback from reaching .NET.</summary>
public sealed class ConnectionStatusHostTests : BunitContext
{
    /// <summary>Registers isolated circuit state and inert test interop before the renderer starts.</summary>
    public ConnectionStatusHostTests()
    {
        Services.AddScoped<ExperienceCoordinator>();
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        Services.AddSingleton(new FoundationAuthenticationSettings(true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null));
        Services.AddSingleton<ExperienceSessionBinding>();
        var authentication = new ServerAuthenticationStateProvider();
        var principal = DevelopmentPersonas.CreatePrincipal(DevelopmentPersonas.All.Single(p => p.Name == "Alice"));
        WorkforceSession.Stamp(principal, DateTimeOffset.UtcNow);
        authentication.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));
        Services.AddSingleton<AuthenticationStateProvider>(authentication);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./Components/Experience/ConnectionStatus.razor.js");
        SetRendererInfo(new("Server", true));
    }

    /// <summary>Two connected transitions with no delivered down notification still perform two awaited reauthorization passes.</summary>
    /// <returns>Completion after awaited callback count, disconnected-state and recovered-zone assertions.</returns>
    [Fact]
    public async Task ReconnectReauthorizesWhenDownCallbackCouldNotReachServer()
    {
        var coordinator = Services.GetRequiredService<ExperienceCoordinator>();
        var checks = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.ReauthorizationRequested += async () =>
        {
            checks++;
            if (checks == 2) await release.Task;
        };
        var component = Render<ConnectionStatus>();
        await component.Instance.ConnectionChangedAsync(true, "Europe/Prague");
        Assert.True(coordinator.CanUseOnlineActions);
        var reconnecting = component.Instance.ConnectionChangedAsync(true, "Europe/Prague");
        Assert.False(reconnecting.IsCompleted);
        release.SetResult();
        await reconnecting;
        Assert.Equal(2, checks);
        Assert.Equal("Europe/Prague", coordinator.BrowserTimeZone);
        await component.Instance.ConnectionChangedAsync(false, null);
        Assert.False(coordinator.CanUseOnlineActions);
        Assert.Equal(2, checks);
    }
}
