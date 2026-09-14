using Bunit;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Application;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure;
using Sidequest.Web.Authentication;
using Sidequest.Web.Components.Events;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.CoreHost;

/// <summary>Verifies the shared Event control gate against static and interactive renderers without database or provider execution.</summary>
public sealed class EventPrerenderTests : BunitContext
{
    /// <summary>Preserves disabled prerender controls even after a load finishes, while interactive controls follow actual operation state.</summary>
    /// <param name="interactive">Whether the renderer has attached event handlers.</param>
    /// <returns>A task completing after a deterministic pending operation and its final render.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventControlsStayDisabledUntilInteractiveAndIdle(bool interactive)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Sidequest"] = "Server=invalid.example.test;Database=NoConnection;Integrated Security=true"
        }).Build();
        Services.AddLogging();
        Services.AddScoped<ICurrentUser, NoIdentity>();
        Services.AddSidequestApplication();
        Services.AddSidequestInfrastructure(configuration);
        Services.AddSidequestDelivery(configuration);
        Services.AddSidequestCoreWorkflows(configuration,
            new(true, DevelopmentPersonas.TenantId, DevelopmentPersonas.WorkforceRole, null));
        SetRendererInfo(new(interactive ? "Server" : "Static", interactive));
        var component = Render<ControlProbe>();
        Assert.Equal(!interactive, component.Find("button").HasAttribute("disabled"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = component.InvokeAsync(() => component.Instance.RunWorkAsync(gate.Task));
        component.Render();
        Assert.True(component.Find("button").HasAttribute("disabled"));
        gate.SetResult();
        await operation;
        component.Render();
        Assert.Equal(!interactive, component.Find("button").HasAttribute("disabled"));
    }

    private sealed class ControlProbe : EventViewBase
    {
        /// <summary>Runs a controlled operation through the actual shared page lifecycle.</summary>
        /// <param name="work">Work held until the test releases it.</param>
        /// <returns>The shared operation completion task.</returns>
        public Task RunWorkAsync(Task work) => RunAsync(_ => work);

        /// <inheritdoc/>
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "disabled", Busy);
            builder.AddContent(2, "Action");
            builder.CloseElement();
        }
    }

    private sealed class NoIdentity : ICurrentUser
    {
        /// <inheritdoc/>
        public ValueTask<UserIdentity?> GetIdentityAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<UserIdentity?>(null);
        }
    }
}
