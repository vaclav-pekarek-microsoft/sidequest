namespace Sidequest.Application.Operations;

/// <summary>Verifies read-only persistence availability and schema compatibility independently of optional external providers.</summary>
public interface IOperationalReadinessProbe
{
    /// <summary>Completes only when the application can use a compatible persisted schema; never creates or migrates it.</summary>
    /// <param name="cancellationToken">Cancels context acquisition and dependency reads.</param>
    /// <returns>A task completing successfully only when persistence is ready.</returns>
    /// <exception cref="OperationalObservationException">Persistence is unavailable or incompatible; only a safe classification crosses this boundary.</exception>
    /// <exception cref="OperationCanceledException">Caller-requested cancellation is observed.</exception>
    public Task ProbeAsync(CancellationToken cancellationToken = default);
}
