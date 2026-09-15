using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Infrastructure.Delivery;

namespace Sidequest.Infrastructure.Background;

/// <summary>Routes opaque claimed work IDs to exactly one registered versioned handler and renews leases during execution.</summary>
/// <param name="queue">Atomic SQL queue.</param>
/// <param name="handlers">Feature-owned completion, bulk and media-cleanup handlers plus delivery-owned change/reminder handlers.</param>
/// <param name="delivery">Per-recipient transport dispatcher.</param>
/// <param name="execution">Scoped current lease proof.</param>
/// <param name="clock">Delay and UTC time seam.</param>
/// <param name="options">Bounded lease settings.</param>
/// <param name="logger">Operational diagnostics without payloads or raw provider errors.</param>
public sealed class DurableWorkRunner(SqlWorkQueue queue, IEnumerable<IBackgroundWorkHandler> handlers,
    DeliveryDispatcher delivery, WorkExecutionContext execution, TimeProvider clock,
    DurableWorkOptions options, ILogger<DurableWorkRunner> logger)
{
    /// <summary>Claims and executes at most one row in the requested durable category.</summary>
    /// <param name="category">outbox, scheduled or delivery.</param>
    /// <param name="cancellationToken">Host shutdown signal.</param>
    /// <returns>True if a claim was attempted, even when the handler failed visibly.</returns>
    public async Task<bool> RunOnceAsync(string category, CancellationToken cancellationToken = default)
    {
        var lease = await queue.ClaimAsync(category, cancellationToken).ConfigureAwait(false);
        if (lease is null)
            return false;
        execution.Lease = lease;
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = RenewAsync(lease, processing, heartbeatStop.Token);
        try
        {
            if (category == "delivery")
                await delivery.ExecuteAsync(lease, processing.Token).ConfigureAwait(false);
            else
            {
                if ((category == "outbox" && lease.Type != WorkTypes.Change) ||
                    (category == "scheduled" && lease.Type is not (WorkTypes.EventCompletion or WorkTypes.QuestCompletion
                        or WorkTypes.BulkMembership or WorkTypes.Reminder or WorkTypes.MediaCleanup)))
                    throw new DeliveryTransportException(TransportOutcome.Permanent, "Unsupported work version or queue category.");
                var matches = handlers.Where(x => string.Equals(x.WorkType, lease.Type, StringComparison.Ordinal)).ToArray();
                if (matches.Length != 1)
                    throw new DeliveryTransportException(TransportOutcome.Permanent, "Unknown work type or ambiguous handler registration.");
                await matches[0].ExecuteAsync(lease.Id, processing.Token).ConfigureAwait(false);
            }
            processing.Token.ThrowIfCancellationRequested();
            await queue.CompleteAsync(lease, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // Worker boundary: every failure stays durable; no exception text, content or credentials enter logs.
            logger.LogWarning("Durable work {WorkId} ({Category}) failed with {FailureType}; attempt {Attempt}.",
                lease.Id, category, error.GetType().Name, lease.Attempts);
            await queue.FailAsync(lease, error, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            execution.Lease = null;
        }
        return true;
    }

    private async Task RenewAsync(WorkLease lease, CancellationTokenSource processing, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.LeaseDuration / 3, clock, cancellationToken).ConfigureAwait(false);
                if (!await queue.RenewAsync(lease, cancellationToken).ConfigureAwait(false))
                {
                    await processing.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogWarning("Lease renewal for {WorkId} failed with {FailureType}.", lease.Id, error.GetType().Name);
            await processing.CancelAsync().ConfigureAwait(false);
        }
    }
}
