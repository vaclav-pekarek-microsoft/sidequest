using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Events;

/// <summary>Quest-side effects of already-authorized Event operations, enlisted in the caller-supplied context and explicit transaction.</summary>
/// <remarks>Implementations must not independently commit, own/dispose the supplied context, or call external providers inside the transaction.
/// The Event operation owns atomic persistence of parent changes, child state/history, and durable delivery work.
/// Calls sharing the same context must be sequential; child cascades do not make tracked entities thread-safe.</remarks>
public interface IQuestEventLifecycle
{
    /// <summary>Cancels applicable Draft, Active, and Suspended child Quests as an explicit parent-cancellation cascade.</summary>
    /// <param name="db">Caller-owned context enlisted in the Event cancellation transaction.</param>
    /// <param name="eventId">Internal Event identifier already authorized for cancellation.</param>
    /// <param name="actorId">Internal initiating actor identifier, or null for a system action.</param>
    /// <param name="reason">Recorded cascade reason for child history and applicable notifications.</param>
    /// <param name="now">Caller-supplied UTC operation instant used consistently across the cascade.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the caller controls rollback.</param>
    /// <returns>A task completing after child effects are prepared in the caller's transaction, not independently committed or externally delivered.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    /// <example>
    /// <code>
    /// // db is already enlisted in the caller's authorized Event cancellation transaction.
    /// await questLifecycle.CancelForEventAsync(
    ///     db, eventId, actorId, reason, now, cancellationToken).ConfigureAwait(false);
    /// // The caller also applies parent status, pending membership cleanup, and audit/outbox changes.
    /// await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    /// await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    /// </code>
    /// </example>
    public Task CancelForEventAsync(ISidequestDbContext db, Guid eventId, Guid? actorId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken = default);
    /// <summary>Performs repeat-safe Event-end cleanup: complete overdue Active/Suspended children and cancel unpublished drafts with a system reason.</summary>
    /// <param name="db">Caller-owned context enlisted in the Event completion transaction.</param>
    /// <param name="eventId">Internal completing Event identifier.</param>
    /// <param name="now">Caller-supplied UTC completion instant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the caller controls rollback.</param>
    /// <returns>A task completing after child history/state cleanup within the supplied transaction, without participant cancellation delivery or an independent commit.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task CompleteForEventAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now, CancellationToken cancellationToken = default);
    /// <summary>Invalidates the removed member's Quest invitations, attendance, and follows while retaining history and queuing applicable calendar withdrawals.</summary>
    /// <param name="db">Caller-owned context enlisted in the membership removal transaction.</param>
    /// <param name="eventId">Internal Event identifier whose membership is being removed.</param>
    /// <param name="userId">Internal account identifier losing individual membership.</param>
    /// <param name="actorId">Internal authorized actor identifier responsible for removal or leaving.</param>
    /// <param name="reason">Recorded membership-loss explanation.</param>
    /// <param name="now">Caller-supplied UTC operation instant.</param>
    /// <param name="cancellationToken">Requests cooperative cancellation; the caller controls rollback.</param>
    /// <returns>A task completing after child cleanup and durable withdrawal intent are prepared, without independently committing or restoring state on rejoin.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task RemoveMemberParticipationAsync(ISidequestDbContext db, Guid eventId, Guid userId, Guid actorId,
        string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
}
