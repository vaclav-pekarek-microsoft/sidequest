namespace Sidequest.Application.Operations;

/// <summary>Immutable aggregate-only observation of one queue at a single sampling cutoff; contains no content or identifiers.</summary>
public sealed record QueueObservation
{
    /// <summary>Creates validated counts and an optional oldest eligibility age, not a business deadline or mailbox-delivery SLA.</summary>
    /// <param name="queue">One of the three fixed operational queues.</param>
    /// <param name="pending">Rows in Pending state, including retries not yet due or awaiting exhaustion handling.</param>
    /// <param name="due">Eligible pending rows plus expired processing leases, with fewer than eight attempts.</param>
    /// <param name="deadLetter">Rows already in DeadLetter state.</param>
    /// <param name="oldestDueAge">Elapsed time since the oldest due row became eligible; null exactly when due is zero.</param>
    /// <exception cref="ArgumentOutOfRangeException">The queue is undefined, a count is negative, or an age is negative.</exception>
    /// <exception cref="ArgumentException">Age presence disagrees with the due count.</exception>
    public QueueObservation(OperationalQueue queue, long pending, long due, long deadLetter, TimeSpan? oldestDueAge)
    {
        if (!Enum.IsDefined(queue))
            throw new ArgumentOutOfRangeException(nameof(queue));
        ArgumentOutOfRangeException.ThrowIfNegative(pending);
        ArgumentOutOfRangeException.ThrowIfNegative(due);
        ArgumentOutOfRangeException.ThrowIfNegative(deadLetter);
        if (oldestDueAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(oldestDueAge));
        if ((due == 0) != (oldestDueAge is null))
            throw new ArgumentException("Oldest eligibility age must exist exactly when due work exists.", nameof(oldestDueAge));
        Queue = queue;
        Pending = pending;
        Due = due;
        DeadLetter = deadLetter;
        OldestDueAge = oldestDueAge;
    }

    /// <summary>The fixed queue dimension, independent of work type or recipient.</summary>
    public OperationalQueue Queue { get; }
    /// <summary>Count of Pending rows at the sampling cutoff.</summary>
    public long Pending { get; }
    /// <summary>Count of rows eligible for a processing attempt at the sampling cutoff.</summary>
    public long Due { get; }
    /// <summary>Count of persisted terminal failures at the sampling cutoff.</summary>
    public long DeadLetter { get; }
    /// <summary>Age of the oldest retry/lease eligibility instant; null when no work is due.</summary>
    public TimeSpan? OldestDueAge { get; }
}
