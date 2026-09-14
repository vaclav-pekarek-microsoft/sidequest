using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Events.Implementation;

/// <summary>Reconciles one versioned Event end job atomically with Quest cleanup; dispatchers retain lease ownership.</summary>
/// <param name="factory">Creates a short-lived SQL context for each execution.</param>
/// <param name="quests">Enlists child completion effects in this handler's transaction.</param>
/// <param name="clock">Supplies the authoritative UTC execution instant.</param>
public sealed class EventCompletionHandler(ISidequestDbContextFactory factory, IQuestEventLifecycle quests,
    TimeProvider clock) : IBackgroundWorkHandler
{
    /// <inheritdoc />
    public string WorkType => WorkTypes.EventCompletion;

    /// <inheritdoc />
    public async Task ExecuteAsync(Guid workId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var work = await db.ScheduledWork.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId,
            cancellationToken).ConfigureAwait(false) ?? throw EventTransactions.Unavailable();
        var payload = EventTransactions.ReadPayload<EventCompletionPayload>(work, WorkType);
        if (payload.SchemaVersion != 1 || payload.EventId == Guid.Empty || payload.ExpectedEndUtc == default)
            throw new DomainException(ErrorCode.Validation, "Event completion payload is invalid or unsupported.");
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        // A deleted unpublished draft cannot have a completion job; missing aggregates are safe obsolete work.
        await db.LockEventAsync(payload.EventId, cancellationToken).ConfigureAwait(false);
        var item = await db.Events.SingleOrDefaultAsync(x => x.Id == payload.EventId, cancellationToken).ConfigureAwait(false);
        if (item is null)
            return;
        if (EventTransactions.End(item) != payload.ExpectedEndUtc)
            return;
        var now = clock.GetUtcNow();
        if (now < payload.ExpectedEndUtc)
            throw EventTransactions.Conflict("Event completion is not due yet.");
        await EventTransactions.CompleteAsync(db, item, quests, now, cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
