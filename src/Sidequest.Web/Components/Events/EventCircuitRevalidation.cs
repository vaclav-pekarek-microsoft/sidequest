using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Sidequest.Web.Components.Events;

/// <summary>Per-circuit reconnect notification that prompts Event pages to reauthorize their protected projections.</summary>
/// <remarks>Register one scoped instance both as this concrete service and as CircuitHandler.
/// Subscribers must marshal to their renderer and unsubscribe when disposed. No user state is shared across circuits.</remarks>
public sealed class EventCircuitRevalidation : CircuitHandler
{
    /// <summary>Asynchronous subscribers recheck server access after connection restoration; every subscriber is awaited.</summary>
    public event Func<Task>? Reconnected;

    /// <inheritdoc />
    public override async Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var callbacks = Reconnected?.GetInvocationList();
        if (callbacks is null)
            return;
        foreach (var callback in callbacks.Cast<Func<Task>>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await callback().ConfigureAwait(false);
        }
    }
}
