using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Stages Event-end reconciliation for already Event-locked callers, without saving or owning their transaction.</summary>
/// <param name="quests">Independent Quest cascade adapter; it must not depend on this reconciler or save its caller's context.</param>
/// <remarks>No current-user service is required for this system transition. The caller supplies the authoritative
/// instant and owns authorization, the Serializable Event lock, persistence, rollback, and context lifetime.</remarks>
public sealed class EventLifecycleReconciler(IQuestEventLifecycle quests) : IEventLifecycleReconciler
{
    /// <inheritdoc />
    public async Task<bool> ReconcileAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        cancellationToken.ThrowIfCancellationRequested();
        if (eventId == Guid.Empty)
            throw new DomainException(ErrorCode.Validation, "An Event identifier is required.", nameof(eventId));
        var item = await db.Events.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken).ConfigureAwait(false)
            ?? throw EventTransactions.Unavailable();
        return await EventTransactions.CompleteAsync(db, item, quests, now, cancellationToken).ConfigureAwait(false);
    }
}
