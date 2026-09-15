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
using Sidequest.Web.Experience;

namespace Sidequest.UnitTests.CoreHost;

/// <summary>Verifies the shared Event control gate against static and interactive renderers without database or provider execution.</summary>
public sealed class EventPrerenderTests : BunitContext
{
    /// <summary>Preserves disabled prerender controls even after a load finishes, while interactive controls follow actual operation state.</summary>
    /// <param name="interactive">Whether the renderer has attached event handlers.</param>
    /// <param name="online">Whether the host has verified the current online connection.</param>
    /// <returns>A task completing after a deterministic pending operation and its final render.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EventControlsStayDisabledUntilInteractiveAndIdle(bool interactive, bool online)
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
        var experience = new ExperienceCoordinator();
        Services.AddSingleton(experience);
        await experience.ReportConnectionAsync(online, null);
        var snapshots = 0;
        experience.SnapshotRefreshRequested += () => { snapshots++; return Task.CompletedTask; };
        SetRendererInfo(new(interactive ? "Server" : "Static", interactive));
        var component = Render<ControlProbe>();
        Assert.Equal(!interactive || !online, component.Find("button").HasAttribute("disabled"));

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = component.InvokeAsync(() => component.Instance.RunWorkAsync(gate.Task));
        component.Render();
        Assert.True(component.Find("button").HasAttribute("disabled"));
        Assert.Equal(0, snapshots);
        gate.SetResult();
        await operation;
        component.Render();
        Assert.Equal(!interactive || !online, component.Find("button").HasAttribute("disabled"));
        Assert.Equal(interactive && online ? 1 : 0, snapshots);
        var mutations = 0;
        await component.InvokeAsync(() => component.Instance.RunMutationWorkAsync(() => { mutations++; return Task.CompletedTask; }));
        Assert.Equal(interactive && online ? 1 : 0, mutations);
        await experience.ReportConnectionAsync(!online, null);
        Assert.Equal(interactive && online ? 1 : 0, mutations);
    }

    private sealed class ControlProbe : EventViewBase
    {
        /// <summary>Runs a controlled operation through the actual shared page lifecycle.</summary>
        /// <param name="work">Work held until the test releases it.</param>
        /// <returns>The shared operation completion task.</returns>
        public Task RunWorkAsync(Task work) => RunAsync(_ => work);

        /// <summary>Exercises the actual no-queue mutation gate independently of browser-disabled controls.</summary>
        /// <param name="work">A callback that must execute only for currently enabled mutation controls.</param>
        /// <returns>The immediate mutation attempt without scheduling later replay.</returns>
        public Task RunMutationWorkAsync(Func<Task> work) => RunMutationAsync(_ => work());

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
