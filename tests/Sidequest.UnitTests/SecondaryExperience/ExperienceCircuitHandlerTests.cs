using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Web.Components.Events;
using Sidequest.Web.Experience;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Verifies transport handshakes cannot await snapshot interop or restore readiness before browser session verification.</summary>
public sealed class ExperienceCircuitHandlerTests
{
    /// <summary>Orders real circuit handlers so an already-rendered Event refresh defers client work until after the handshake, even when the down signal was missed.</summary>
    /// <param name="receiveDown">Whether the server receives a down callback before reconnecting.</param>
    /// <returns>A task completing after synchronous handshake callbacks and the subsequently released snapshot refresh.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconnectDefersRenderedEventSnapshotUntilVerifiedBrowserConfirmation(bool receiveDown)
    {
        var services = new ServiceCollection();
        services.AddEventUiRevalidation();
        services.AddSidequestExperience();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<ExperienceCoordinator>();
        await coordinator.ReportConnectionAsync(true, "Europe/Prague");
        var handlers = scope.ServiceProvider.GetServices<CircuitHandler>().OrderBy(handler => handler.Order).ToArray();
        Assert.Collection(handlers,
            handler => Assert.IsType<ExperienceCircuitHandler>(handler),
            handler => Assert.IsType<EventCircuitRevalidation>(handler));
        var changed = 0;
        var reauthorizations = 0;
        var snapshots = 0;
        var eventRefreshes = 0;
        var handshakeCompleted = false;
        coordinator.SnapshotRefreshRequested += () =>
        {
            Assert.True(handshakeCompleted, "Snapshot interop must not run while ConnectCircuit awaits lifecycle handlers.");
            snapshots++;
            return Task.CompletedTask;
        };
        await using var view = new ExperienceViewSubscription(coordinator,
            () => { changed++; return Task.CompletedTask; },
            () => { reauthorizations++; return Task.CompletedTask; });
        await view.AfterRenderAsync();
        var events = scope.ServiceProvider.GetRequiredService<EventCircuitRevalidation>();
        events.Reconnected += () =>
        {
            eventRefreshes++;
            Assert.False(coordinator.CanUseOnlineActions);
            return view.AfterOperationAsync();
        };

        // Circuit is framework-owned and neither handler inspects it, matching the existing circuit-handler fixtures.
        if (receiveDown)
        {
            foreach (var handler in handlers)
                await handler.OnConnectionDownAsync(null!, CancellationToken.None);
            Assert.False(coordinator.CanUseOnlineActions);
        }
        foreach (var handler in handlers)
        {
            var callback = handler.OnConnectionUpAsync(null!, CancellationToken.None);
            Assert.True(callback.IsCompletedSuccessfully, "The transport handshake must not wait for browser work.");
            await callback;
        }
        Assert.Equal(1, eventRefreshes);
        Assert.False(coordinator.CanUseOnlineActions);
        Assert.Equal("Europe/Prague", coordinator.BrowserTimeZone);
        Assert.Equal(0, changed);
        Assert.Equal(0, reauthorizations);
        Assert.Equal(0, snapshots);
        await view.AfterRenderAsync();
        Assert.Equal(0, snapshots);

        handshakeCompleted = true;
        await coordinator.ReportConnectionAsync(true, "Europe/Prague");
        Assert.True(coordinator.CanUseOnlineActions);
        Assert.Equal(1, changed);
        Assert.Equal(1, reauthorizations);
        Assert.Equal(1, snapshots);
        await view.AfterRenderAsync();
        Assert.Equal(2, snapshots);
        await view.AfterRenderAsync();
        Assert.Equal(2, snapshots);
    }

    /// <summary>Even a cancelled transport callback invalidates only its own circuit and never enables either circuit automatically.</summary>
    /// <param name="connectionUp">Whether to exercise the up callback instead of the down callback.</param>
    /// <returns>A task completing after scoped identity, cancellation cleanup and independent readiness assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportInvalidationIsScopedAndCannotBeSkippedByCancellation(bool connectionUp)
    {
        var services = new ServiceCollection();
        services.AddSidequestExperience();
        await using var provider = services.BuildServiceProvider();
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var firstCoordinator = first.ServiceProvider.GetRequiredService<ExperienceCoordinator>();
        var secondCoordinator = second.ServiceProvider.GetRequiredService<ExperienceCoordinator>();
        await firstCoordinator.ReportConnectionAsync(true, "Europe/Prague");
        await secondCoordinator.ReportConnectionAsync(true, "America/New_York");
        var firstHandler = Assert.Single(first.ServiceProvider.GetServices<CircuitHandler>());
        var secondHandler = Assert.Single(second.ServiceProvider.GetServices<CircuitHandler>());
        Assert.Same(firstHandler, Assert.Single(first.ServiceProvider.GetServices<CircuitHandler>()));
        Assert.NotSame(firstHandler, secondHandler);
        var callbacks = 0;
        firstCoordinator.Changed += UnexpectedCallback;
        firstCoordinator.ReauthorizationRequested += UnexpectedCallback;
        firstCoordinator.SnapshotRefreshRequested += UnexpectedCallback;

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await (connectionUp
            ? firstHandler.OnConnectionUpAsync(null!, cancellation.Token)
            : firstHandler.OnConnectionDownAsync(null!, cancellation.Token));
        Assert.False(firstCoordinator.CanUseOnlineActions);
        Assert.True(secondCoordinator.CanUseOnlineActions);
        Assert.Equal("Europe/Prague", firstCoordinator.BrowserTimeZone);
        Assert.Equal("America/New_York", secondCoordinator.BrowserTimeZone);
        Assert.Equal(0, callbacks);
        await firstHandler.OnConnectionUpAsync(null!, CancellationToken.None);
        Assert.False(firstCoordinator.CanUseOnlineActions);
        Assert.True(secondCoordinator.CanUseOnlineActions);
        Assert.Equal(0, callbacks);

        Task UnexpectedCallback()
        {
            callbacks++;
            return Task.CompletedTask;
        }
    }
}
