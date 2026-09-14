using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Notifications.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.Infrastructure.Delivery;

namespace Sidequest.Infrastructure.Background;

/// <summary>Produces at most one logical reminder per attendee/start revision after current time, access and preference checks.</summary>
/// <param name="factory">Operation-scoped SQL contexts.</param>
/// <param name="policy">Recipient authorization.</param>
/// <param name="execution">Current worker lease proof.</param>
/// <param name="options">Maximum permitted lateness.</param>
/// <param name="clock">UTC clock.</param>
public sealed class ReminderWorkHandler(ISidequestDbContextFactory factory, RecipientPolicy policy,
    WorkExecutionContext execution, DurableWorkOptions options, TimeProvider clock) : IBackgroundWorkHandler
{
    /// <inheritdoc/>
    public string WorkType => WorkTypes.Reminder;

    /// <inheritdoc/>
    public async Task ExecuteAsync(Guid workId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var preflight = await db.ScheduledWork.AsNoTracking().SingleAsync(x => x.Id == workId, cancellationToken).ConfigureAwait(false);
        if (execution.Lease is not { } capturedLease || capturedLease.Id != workId || capturedLease.Category != "scheduled" ||
            preflight.Status != WorkStatus.Processing ||
            preflight.LeaseId != capturedLease.Token || preflight.LeaseUntilUtc <= clock.GetUtcNow())
            return;
        var eventId = await db.Quests.AsNoTracking().Where(x => x.Id == preflight.QuestId).Select(x => (Guid?)x.EventId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw ChangeOutboxHandler.InvalidPayload();
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var row = await db.ScheduledWork.SingleAsync(x => x.Id == workId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (execution.Lease is not { } lease || lease.Id != workId || lease.Category != "scheduled" || row.Status != WorkStatus.Processing ||
            row.LeaseId != lease.Token || row.LeaseUntilUtc <= now)
            return;
        var payload = ReminderPayload.Parse(row.PayloadJson);
        if (row.Type != WorkTypes.Reminder || payload.QuestId != row.QuestId || payload.UserId != row.UserId ||
            row.QuestId != preflight.QuestId ||
            payload.StartRevision < 0 || payload.QuestId == Guid.Empty || payload.UserId == Guid.Empty)
            throw ChangeOutboxHandler.InvalidPayload();
        var quest = await db.Quests.SingleOrDefaultAsync(x => x.Id == payload.QuestId, cancellationToken).ConfigureAwait(false);
        if (quest is null || quest.StartRevision != payload.StartRevision || quest.StartUtc != payload.StartUtc ||
            quest.Status != QuestStatus.Active || quest.StartUtc <= now || payload.ScheduledUtc > now ||
            now - payload.ScheduledUtc > options.ReminderLateness)
            return;
        var parent = await db.Events.SingleAsync(x => x.Id == quest.EventId, cancellationToken).ConfigureAwait(false);
        var p = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.UserId == payload.UserId, cancellationToken).ConfigureAwait(false);
        if (parent.Status != EventStatus.Active ||
            TimeRules.EventWindow(parent.StartDate, parent.EndDate, parent.TimeZoneId).End <= now ||
            p is { RemindersEnabled: false } ||
            !await policy.CanReadQuestAsync(db, quest, payload.UserId, cancellationToken).ConfigureAwait(false) ||
            !await db.Participations.AnyAsync(x => x.QuestId == quest.Id && x.UserId == payload.UserId &&
                x.Status == ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false))
            return;
        if (await db.Notifications.AnyAsync(x => x.SourceChangeId == row.Id && x.UserId == payload.UserId &&
            x.Kind == NotificationKind.Reminder, cancellationToken).ConfigureAwait(false))
            return;
        var change = new ChangeEnvelope(row.Id, NotificationKind.Reminder, quest.EventId, quest.Id, null,
            [payload.UserId], now);
        var notification = new Notification
        {
            SourceChangeId = row.Id, UserId = payload.UserId, Kind = NotificationKind.Reminder,
            EventId = quest.EventId, QuestId = quest.Id, CreatedUtc = now,
            Summary = NotificationRules.Summary(NotificationKind.Reminder)
        };
        db.Notifications.Add(notification);
        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            NotificationId = notification.Id, UserId = payload.UserId,
            DeduplicationKey = $"reminder:{quest.Id:N}:{payload.UserId:N}:{payload.StartRevision}:email",
            DueUtc = now, PayloadJson = JsonSerializer.Serialize(new DeliveryPayload(1, change, payload.UserId, false))
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
