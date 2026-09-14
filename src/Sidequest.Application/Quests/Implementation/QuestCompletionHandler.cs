using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Quests.Implementation;

/// <summary>Completes due Quests idempotently without withdrawing historical calendars or managing worker leases.</summary>
/// <param name="factory">Creates one isolated unit of work per execution.</param>
/// <param name="writer">Stages durable business changes within the transaction.</param>
/// <param name="clock">Authoritative UTC clock, controllable in tests.</param>
/// <param name="eventLifecycle">Reconciles overdue parent state using the same Event-first transaction.</param>
public sealed class QuestCompletionHandler(ISidequestDbContextFactory factory, IChangeWriter writer,
    TimeProvider clock, IEventLifecycleReconciler eventLifecycle) : IBackgroundWorkHandler
{
    /// <inheritdoc />
    public string WorkType => WorkTypes.QuestCompletion;

    /// <inheritdoc />
    /// <remarks>The payload captures the Quest end; ScheduledWork.DueUtc may change independently for retries or administrative replay.
    /// Premature execution fails explicitly so the dispatcher cannot acknowledge an unfinished completion intent.</remarks>
    public async Task ExecuteAsync(Guid workId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var work = await db.ScheduledWork.AsNoTracking().SingleOrDefaultAsync(x => x.Id == workId, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCode.NotFound, "Completion work is unavailable.");
        if (work.Type != WorkType)
            throw new DomainException(ErrorCode.Validation, "Unexpected completion work type.");
        QuestCompletionPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<QuestCompletionPayload>(work.PayloadJson)
                ?? throw new DomainException(ErrorCode.Validation, "Invalid completion payload.");
        }
        catch (JsonException)
        {
            throw new DomainException(ErrorCode.Validation, "Invalid completion payload.");
        }
        if (payload.QuestId == Guid.Empty || payload.EndUtc == default || work.QuestId != payload.QuestId)
            throw new DomainException(ErrorCode.Validation, "Invalid completion payload.");
        var parentId = await QuestChanges.ParentIdAsync(db, payload.QuestId, cancellationToken).ConfigureAwait(false);
        if (parentId is null)
            return;
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(parentId.Value, cancellationToken).ConfigureAwait(false);
        var quest = await db.Quests.SingleOrDefaultAsync(x => x.Id == payload.QuestId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await eventLifecycle.ReconcileAsync(db, parentId.Value, now, cancellationToken).ConfigureAwait(false);
        if (quest is not null && quest.EndUtc == payload.EndUtc &&
            quest.Status is QuestStatus.Active or QuestStatus.Suspended)
        {
            if (now < quest.EndUtc)
                throw new DomainException(ErrorCode.Conflict, "Quest completion is not due yet.");
            await QuestChanges.TransitionAsync(db, writer, quest, QuestStatus.Completed, null,
                "The Quest end time has been reached.", now, cancellationToken).ConfigureAwait(false);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
