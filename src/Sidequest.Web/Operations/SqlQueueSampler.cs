using Sidequest.Application.Operations;

namespace Sidequest.Web.Operations;

/// <summary>Coordinates a complete queue observation through a provider-neutral reader without accessing persistence internals.</summary>
/// <param name="reader">Reads aggregate-only observations or returns a safe typed failure through an exception.</param>
/// <exception cref="ArgumentNullException">The reader is null.</exception>
public sealed class SqlQueueSampler(IOperationalQueueReader reader)
{
    private readonly IOperationalQueueReader reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>Samples all queues or fails; cancellation prevents publication of late results.</summary>
    /// <param name="cancellationToken">Cancels dependency reads and observation publication.</param>
    /// <returns>Exactly three immutable observations in outbox, scheduled, delivery order.</returns>
    /// <exception cref="OperationalObservationException">A complete observation is unavailable; no partial or healthy-zero fallback is returned.</exception>
    /// <exception cref="OperationCanceledException">Caller-requested cancellation is observed.</exception>
    public async Task<IReadOnlyList<QueueObservation>> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observations = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return observations;
    }
}
