using Sidequest.Application.Abstractions;

namespace Sidequest.Application.Events;

/// <summary>Stages overdue Event completion and its required membership and child-Quest cleanup.</summary>
/// <remarks>The Events module owns this behavior. The caller must hold the Event mutation lock in an explicit
/// Serializable transaction. Implementations never save, commit, dispose the context, or call external providers.
/// Persist system reconciliation before evaluating a requested command that may be rejected, without committing
/// any unvalidated user mutation. Quest-side cascade implementations must not depend on this port.</remarks>
public interface IEventLifecycleReconciler
{
    /// <summary>Stages repeat-safe overdue completion, pending-request/invitation cleanup, and child lifecycle effects.</summary>
    /// <param name="db">Caller-owned, Event-locked transaction context; it must not be used concurrently.</param>
    /// <param name="eventId">Internal Event identifier whose lifecycle is being reconciled.</param>
    /// <param name="now">Authoritative UTC operation instant used consistently for the completion boundary and history.</param>
    /// <param name="cancellationToken">Requests cancellation; the caller retains rollback responsibility.</param>
    /// <returns>Whether completion-related changes were staged and require persistence by the caller.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The Event is unavailable or persistence encounters a conflict.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<bool> ReconcileAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
