namespace Sidequest.Application.Operations;

/// <summary>Reads complete aggregate-only queue observations outside application transactions without altering work or calling delivery providers.</summary>
public interface IOperationalQueueReader
{
    /// <summary>Reads all fixed queues at a common eligibility cutoff or fails without returning partial or empty fallback data.</summary>
    /// <param name="cancellationToken">Cancels context acquisition and dependency reads.</param>
    /// <returns>Exactly one immutable observation per queue in outbox, scheduled, delivery order; empty queues have actual zero counts.</returns>
    /// <exception cref="OperationalObservationException">A complete observation is unavailable; the failure contains no raw provider exception.</exception>
    /// <exception cref="OperationCanceledException">Caller-requested cancellation is observed.</exception>
    /// <remarks>Counts describe existing pending/retry/lease states, not business deadlines or completed-delivery latency. Observations are not a cross-queue transactional snapshot.</remarks>
    public Task<IReadOnlyList<QueueObservation>> ReadAsync(CancellationToken cancellationToken = default);
}
