using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Background;

namespace Sidequest.Infrastructure.Delivery;

/// <summary>Consumes version-one business changes into deduplicated inbox, transport intent and reminder schedules atomically.</summary>
/// <param name="factory">Operation context factory.</param>
/// <param name="policy">Current recipient eligibility policy.</param>
/// <param name="scheduler">Reminder schedule reconciler.</param>
/// <param name="execution">Current scoped lease proof.</param>
/// <param name="clock">UTC clock.</param>
public sealed class ChangeOutboxHandler(ISidequestDbContextFactory factory, RecipientPolicy policy,
    ReminderScheduler scheduler, WorkExecutionContext execution, TimeProvider clock) : IBackgroundWorkHandler
{
    /// <inheritdoc/>
    public string WorkType => WorkTypes.Change;

    /// <inheritdoc/>
    public async Task ExecuteAsync(Guid workId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var preflight = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == workId, cancellationToken).ConfigureAwait(false);
        if (!Owns(preflight, execution.Lease, clock.GetUtcNow()))
            return;
        var captured = Parse(preflight.PayloadJson);
        ValidateRow(preflight, captured, workId);
        if (captured.QuestId is Guid capturedQuestId)
        {
            var parentId = await db.Quests.AsNoTracking().Where(x => x.Id == capturedQuestId).Select(x => (Guid?)x.EventId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (parentId != captured.EventId)
                throw InvalidPayload();
        }
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(captured.EventId, cancellationToken).ConfigureAwait(false);
        var row = await db.OutboxMessages.SingleAsync(x => x.Id == workId, cancellationToken).ConfigureAwait(false);
        if (!Owns(row, execution.Lease, clock.GetUtcNow()))
            return;
        var change = Parse(row.PayloadJson);
        ValidateRow(row, change, workId);
        if (change.EventId != captured.EventId || change.QuestId != captured.QuestId || change.ChangeId != captured.ChangeId)
            throw InvalidPayload();
        var quest = change.QuestId is Guid questId
            ? await db.Quests.SingleOrDefaultAsync(x => x.Id == questId, cancellationToken).ConfigureAwait(false) : null;
        if (quest is not null && quest.EventId != change.EventId)
            throw InvalidPayload();
        if (change.Kind == NotificationKind.QuestPublished && quest?.Visibility == QuestVisibility.Public)
        {
            // The snapshot is persisted with all effects. A crash before commit has produced no recipients.
            if (change.RecipientIds.Length == 0)
            {
                var recipients = await db.Users.Where(u => u.IsEligible && u.DepartureVerifiedUtc == null &&
                    u.LastSignedInUtc != null && u.Id != change.ActorId &&
                    db.EventMemberships.Any(m => m.EventId == change.EventId && m.UserId == u.Id &&
                        m.Status == MembershipStatus.Active)).Select(u => u.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                change = change with { RecipientIds = recipients };
                row.PayloadJson = JsonSerializer.Serialize(change);
            }
        }
        var includesWithdrawals = change.Kind is NotificationKind.AccessRemoved or NotificationKind.Left or NotificationKind.AttendeeRemoved
            or NotificationKind.QuestSuspended or NotificationKind.QuestCancelled or NotificationKind.EventCancelled;
        var recipientsToProcess = change.RecipientIds.Concat(includesWithdrawals ? change.PreviousAttendeeIds ?? [] : []).Distinct().ToArray();
        // Match reminder consumers: reserve schedules before inbox keys, then calendar intent.
        if (quest is not null)
        {
            var participants = await db.Participations.Where(x => x.QuestId == quest.Id).Select(x => x.UserId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var userId in participants.Concat(recipientsToProcess).Distinct())
            {
                var preference = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken).ConfigureAwait(false);
                await scheduler.ReconcileAsync(db, quest, userId, preference, cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (var recipientId in recipientsToProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await db.HasNotificationForUpdateAsync(change.ChangeId, recipientId, change.Kind, cancellationToken).ConfigureAwait(false))
                continue;
            if (!await policy.EligibleAsync(db, recipientId, cancellationToken).ConfigureAwait(false))
                continue;
            var canReceive = await policy.CanReceiveAsync(db, change, recipientId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var calendar = quest is null ? null :
                await StageCalendarAsync(db, quest, change, recipientId, cancellationToken).ConfigureAwait(false);
            if (!canReceive && calendar is null)
                continue;
            var generic = IsGeneric(change.Kind) || !canReceive;
            var notification = new Notification
            {
                SourceChangeId = change.ChangeId,
                UserId = recipientId,
                Kind = change.Kind,
                EventId = change.EventId,
                QuestId = change.QuestId,
                CreatedUtc = change.OccurredUtc,
                Summary = NotificationRules.Summary(change.Kind),
                IsAccessLossNotice = generic
            };
            db.Notifications.Add(notification);
            var joined = quest is not null && await db.Participations.AnyAsync(p => p.QuestId == quest.Id &&
                p.UserId == recipientId && p.Status == ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false);
            var mandatory = IsMandatory(change, joined);
            var observerOnly = IsTargeted(change.Kind) && !change.AffectedUserIds!.Contains(recipientId);
            if (!observerOnly && change.Kind != NotificationKind.SuspendedQuestEdited)
            {
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    NotificationId = notification.Id,
                    UserId = recipientId,
                    DeduplicationKey = $"change:{change.ChangeId:N}:{recipientId:N}:email",
                    DueUtc = clock.GetUtcNow(),
                    PayloadJson = JsonSerializer.Serialize(new DeliveryPayload(1, change, recipientId,
                        mandatory || calendar is not null, calendar))
                });
            }
        }
        if (!Owns(row, execution.Lease, clock.GetUtcNow()))
            throw new DeliveryTransportException(TransportOutcome.Retryable, "Outbox lease expired before effects could commit.");
        // Completion shares the effects transaction. Even an empty publication snapshot cannot be re-expanded after a crash.
        row.Status = WorkStatus.Completed;
        row.LeaseId = null;
        row.LeaseUntilUtc = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CalendarSnapshot?> StageCalendarAsync(ISidequestDbContext db, Quest quest,
        ChangeEnvelope change, Guid recipientId, CancellationToken cancellationToken)
    {
        if (await db.QuestStatusHistory.AnyAsync(x => x.QuestId == quest.Id &&
            x.Previous == QuestStatus.Draft && x.Next == QuestStatus.Cancelled, cancellationToken).ConfigureAwait(false) ||
            await db.EventStatusHistory.AnyAsync(x => x.EventId == quest.EventId &&
                x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled, cancellationToken).ConfigureAwait(false))
            return null;
        var previous = change.PreviousAttendeeIds?.Contains(recipientId) ?? false;
        var joined = await db.Participations.AnyAsync(p => p.QuestId == quest.Id && p.UserId == recipientId &&
            p.Status == ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false);
        var withdrawal = change.Kind is NotificationKind.AccessRemoved or NotificationKind.Left or NotificationKind.AttendeeRemoved
            or NotificationKind.QuestSuspended or NotificationKind.QuestCancelled or NotificationKind.EventCancelled;
        if (change.Kind == NotificationKind.QuestSuspended && quest.Status == QuestStatus.Active)
            return null;
        var request = change.Kind is NotificationKind.Joined or NotificationKind.QuestReinstated ||
            (change.Kind == NotificationKind.QuestUpdated && change.CalendarChanged);
        if (IsTargeted(change.Kind) && !change.AffectedUserIds!.Contains(recipientId))
            return null;
        if (!withdrawal && !request)
            return null;
        var state = await db.FindCalendarDeliveryStateForUpdateAsync(quest.Id, recipientId, cancellationToken).ConfigureAwait(false);
        if (withdrawal && !previous && state is null)
            return null;
        // A stale attendance-specific withdrawal cannot cancel a later explicit rejoin.
        if (withdrawal && change.Kind is NotificationKind.Left or NotificationKind.AttendeeRemoved or NotificationKind.AccessRemoved &&
            joined && await policy.CanReadQuestAsync(db, quest, recipientId, cancellationToken).ConfigureAwait(false))
            return null;
        if (request)
        {
            var parent = await db.Events.SingleAsync(x => x.Id == quest.EventId, cancellationToken).ConfigureAwait(false);
            if (!joined || quest.Status != QuestStatus.Active || parent.Status != EventStatus.Active ||
                quest.EndUtc <= clock.GetUtcNow() ||
                !await policy.CanReadQuestAsync(db, quest, recipientId, cancellationToken).ConfigureAwait(false))
                return null;
        }
        if (change.CalendarRevision <= 0)
            throw InvalidPayload();
        var sequence = request ? Math.Max(change.CalendarRevision, quest.CalendarRevision) : change.CalendarRevision;
        if (state is not null && sequence <= state.IntendedSequence)
            return null;
        var user = await db.Users.SingleAsync(x => x.Id == recipientId, cancellationToken).ConfigureAwait(false);
        CalendarSnapshot? prior = null;
        if (withdrawal && state is not null)
        {
            try { prior = JsonSerializer.Deserialize<CalendarSnapshot>(state.Payload); }
            catch (JsonException) { throw InvalidPayload(); }
        }
        var snapshot = new CalendarSnapshot(quest.Id, sequence, change.OccurredUtc, prior?.StartUtc ?? quest.StartUtc, prior?.EndUtc ?? quest.EndUtc,
            user.Email, withdrawal ? "CANCEL" : "REQUEST",
            withdrawal ? "Sidequest appointment withdrawn" : quest.Title,
            withdrawal ? "" : quest.Description, withdrawal ? "" : quest.Location);
        if (state is null)
        {
            state = new CalendarDeliveryState { QuestId = quest.Id, UserId = recipientId };
            db.CalendarDeliveryStates.Add(state);
        }
        state.IntendedSequence = sequence;
        state.IntendedMethod = snapshot.Method;
        state.Payload = JsonSerializer.Serialize(snapshot);
        state.ChangedUtc = clock.GetUtcNow();
        return snapshot;
    }

    internal static ChangeEnvelope Parse(string json)
    {
        try
        {
            var change = JsonSerializer.Deserialize<ChangeEnvelope>(json) ?? throw InvalidPayload();
            Validate(change);
            return change;
        }
        catch (JsonException)
        {
            throw InvalidPayload();
        }
    }

    internal static void Validate(ChangeEnvelope change)
    {
        if (change.ChangeId == Guid.Empty || change.EventId == Guid.Empty ||
                change.RecipientIds is null || change.RecipientIds.Any(x => x == Guid.Empty) ||
                (change.PreviousAttendeeIds?.Any(x => x == Guid.Empty) ?? false) ||
                !Enum.IsDefined(change.Kind) || change.CalendarRevision < 0 ||
                change.OccurredUtc == default || change.OccurredUtc.Offset != TimeSpan.Zero ||
                change.QuestId == Guid.Empty)
            throw InvalidPayload();
        if (change.QuestId is null && change.Kind is NotificationKind.Joined or NotificationKind.Left or
            NotificationKind.AttendeeRemoved or NotificationKind.QuestPublished or NotificationKind.QuestInvitation or
            NotificationKind.QuestUpdated or NotificationKind.QuestSuspended or NotificationKind.QuestReinstated or
            NotificationKind.QuestCancelled or NotificationKind.Reminder or NotificationKind.SuspendedQuestEdited)
            throw InvalidPayload();
        ValidateTargets(change);
    }

    internal static DeliveryTransportException InvalidPayload() => new(TransportOutcome.Permanent, "Unsupported or malformed durable payload.");

    private static void ValidateRow(OutboxMessage row, ChangeEnvelope change, Guid workId)
    {
        if (row.Type != WorkTypes.Change || row.SchemaVersion != 1 || row.Id != workId ||
            change.ChangeId != row.Id || row.AggregateId != (change.QuestId ?? change.EventId))
            throw InvalidPayload();
    }

    internal static void ValidateTargets(ChangeEnvelope change)
    {
        if (IsTargeted(change.Kind) && change.AffectedUserIds is not { Length: > 0 })
            throw InvalidPayload();
        if (change.AffectedUserIds?.Any(x => x == Guid.Empty || !change.RecipientIds.Contains(x)) ?? false)
            throw InvalidPayload();
    }

    internal static bool IsTargeted(NotificationKind kind) => kind is NotificationKind.MembershipAdded or
        NotificationKind.MembershipDecided or NotificationKind.Joined or NotificationKind.Left or
        NotificationKind.AttendeeRemoved or NotificationKind.AccessRemoved;

    private static bool Owns(OutboxMessage row, WorkLease? lease, DateTimeOffset now) =>
        lease is not null && row.Id == lease.Id && row.Status == WorkStatus.Processing &&
        row.LeaseId == lease.Token && row.LeaseUntilUtc > now;

    private static bool IsGeneric(NotificationKind kind) => kind is NotificationKind.AccessRemoved or NotificationKind.Left
        or NotificationKind.AttendeeRemoved or NotificationKind.EventInvitation or NotificationKind.MembershipDecided
        or NotificationKind.OwnershipChanged or NotificationKind.SuspendedQuestEdited;

    private static bool IsMandatory(ChangeEnvelope change, bool joined) => change.Kind switch
    {
        NotificationKind.QuestPublished or NotificationKind.Reminder => false,
        NotificationKind.QuestUpdated => change.MaterialChange && joined,
        NotificationKind.SuspendedQuestEdited => false,
        _ => true
    };
}
