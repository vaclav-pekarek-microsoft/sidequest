using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Sidequest.Web.Experience;

/// <summary>Invalidates online readiness before circuit lifecycle subscribers can request browser interop.</summary>
/// <param name="coordinator">The same circuit-scoped coordinator consumed by views and the cookie-checking browser bridge.</param>
/// <remarks>Transport callbacks only invalidate state. They never notify views, invoke JavaScript or enable actions; the browser completes its current-cookie check after the handshake.</remarks>
internal sealed class ExperienceCircuitHandler(ExperienceCoordinator coordinator) : CircuitHandler
{
    /// <summary>Runs before ordinary view reconnect handlers, whose completion may otherwise wait for client interop.</summary>
    public override int Order => int.MinValue;

    /// <inheritdoc />
    /// <remarks>Also invalidates when a browser's down notification never reached the server. Cancellation does not skip fail-closed state cleanup.</remarks>
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        coordinator.SuspendForTransport();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>A connected transport is not yet a completed circuit handshake or a verified current-cookie session.</remarks>
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        coordinator.SuspendForTransport();
        return Task.CompletedTask;
    }
}
