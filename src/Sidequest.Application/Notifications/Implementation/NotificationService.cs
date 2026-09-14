using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Recipient inbox, preference, calendar recovery and redacted administrator recovery operations.</summary>
/// <param name="factory">Creates a fresh context per operation.</param>
/// <param name="access">Current database-backed actor authorization.</param>
/// <param name="policy">Recipient disclosure predicates.</param>
/// <param name="scheduler">Transactional reminder replacement.</param>
/// <param name="calendar">Configured recipient-only renderer.</param>
/// <param name="clock">Current UTC time seam.</param>
public sealed class NotificationService(ISidequestDbContextFactory factory, IResourceAccess access,
    RecipientPolicy policy, ReminderScheduler scheduler, IRecipientCalendarRenderer calendar, TimeProvider clock) : INotificationService
{
    /// <inheritdoc/>
    public async Task<PageResult<NotificationSummary>> ListAsync(PageRequest page, CancellationToken cancellationToken = default)
    {
        var offset = page.Offset;
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var query = policy.Visible(db, actor.Id, clock.GetUtcNow());
        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(x => x.CreatedUtc).ThenBy(x => x.Id)
            .Skip(offset).Take(page.Limit)
            .Select(x => new NotificationSummary(x.Id, x.Kind, x.IsAccessLossNotice ? NotificationRules.Summary(x.Kind) : x.Summary,
                x.IsAccessLossNotice ? null : x.EventId, x.IsAccessLossNotice ? null : x.QuestId,
                x.CreatedUtc, x.ReadUtc != null)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new(items, count, page.Page, page.Limit);
    }

    /// <inheritdoc/>
    public async Task<int> UnreadCountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        return await policy.Visible(db, actor.Id, clock.GetUtcNow()).CountAsync(x => x.ReadUtc == null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task MarkReadAsync(Guid? notificationId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var preflightActor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var eventIds = await NotificationEventIdsAsync(db, preflightActor.Id, notificationId, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await LockEventsAsync(db, eventIds, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var currentEventIds = await NotificationEventIdsAsync(db, actor.Id, notificationId, cancellationToken).ConfigureAwait(false);
        if (actor.Id != preflightActor.Id || !eventIds.SequenceEqual(currentEventIds))
            throw ScopeChanged();
        var query = policy.Visible(db, actor.Id, clock.GetUtcNow());
        if (notificationId is Guid id)
            query = query.Where(x => x.Id == id);
        var items = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (notificationId is not null && items.Count == 0)
            throw Unavailable();
        foreach (var item in items)
            item.ReadUtc ??= clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<PreferenceInput> GetPreferencesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var p = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.UserId == actor.Id, cancellationToken).ConfigureAwait(false);
        return p is null ? new(false, true, true, 1, null) :
            new(p.NewQuestEmail, p.ActivityEmail, p.RemindersEnabled, p.ReminderHours, p.TimeZoneId);
    }

    /// <inheritdoc/>
    public async Task SavePreferencesAsync(PreferenceInput input, CancellationToken cancellationToken = default)
    {
        NotificationRules.Validate(input);
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var preflightActor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var eventIds = await JoinedEventIdsAsync(db, preflightActor.Id, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await LockEventsAsync(db, eventIds, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var currentEventIds = await JoinedEventIdsAsync(db, actor.Id, cancellationToken).ConfigureAwait(false);
        if (actor.Id != preflightActor.Id || !eventIds.SequenceEqual(currentEventIds))
            throw ScopeChanged();
        var p = await db.NotificationPreferences.SingleOrDefaultAsync(x => x.UserId == actor.Id, cancellationToken).ConfigureAwait(false);
        if (p is null)
        {
            p = new NotificationPreference { UserId = actor.Id };
            db.NotificationPreferences.Add(p);
        }
        p.NewQuestEmail = input.NewQuestEmail;
        p.ActivityEmail = input.ActivityEmail;
        p.RemindersEnabled = input.RemindersEnabled;
        p.ReminderHours = input.ReminderHours;
        p.TimeZoneId = input.TimeZoneId;
        await scheduler.ReconcileUserAsync(db, actor.Id, p, cancellationToken).ConfigureAwait(false);
        Audit(db, actor.Id, actor.Id, "notification.preferences.changed");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SetEventNewQuestEmailAsync(Guid eventId, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        await access.RequireEventAsync(db, eventId, actor.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!await policy.AllowsEventDisclosureAsync(db, eventId, actor.Id, cancellationToken).ConfigureAwait(false))
            throw Unavailable();
        var p = await db.EventNotificationPreferences.SingleOrDefaultAsync(x => x.EventId == eventId && x.UserId == actor.Id,
            cancellationToken).ConfigureAwait(false);
        if (p is null)
        {
            p = new EventNotificationPreference { UserId = actor.Id, EventId = eventId };
            db.EventNotificationPreferences.Add(p);
        }
        p.NewQuestEmail = enabled;
        Audit(db, actor.Id, eventId, "notification.event-preference.changed");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string> DownloadCalendarAsync(Guid questId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var eventId = await db.Quests.AsNoTracking().Where(x => x.Id == questId).Select(x => (Guid?)x.EventId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? throw Unavailable();
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var q = await access.RequireQuestAsync(db, questId, actor.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
        var parent = await db.Events.SingleAsync(x => x.Id == q.EventId, cancellationToken).ConfigureAwait(false);
        if (q.Status != QuestStatus.Active || parent.Status != EventStatus.Active || q.EndUtc <= clock.GetUtcNow() ||
            TimeRules.EventWindow(parent.StartDate, parent.EndDate, parent.TimeZoneId).End <= clock.GetUtcNow() ||
            !await policy.CanReadQuestAsync(db, q, actor.Id, cancellationToken).ConfigureAwait(false) ||
            !await db.Participations.AnyAsync(x => x.QuestId == q.Id && x.UserId == actor.Id &&
                x.Status == ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false))
            throw Unavailable();
        var content = calendar.Render(new(q.Id, q.CalendarRevision, q.UpdatedUtc, q.StartUtc,
            q.EndUtc, actor.Email, "REQUEST", q.Title, q.Description, q.Location));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return content;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DeliveryFailure>> FailedDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        // Do not return persisted arbitrary error strings, payloads, addresses or resource metadata.
        var deliveries = await db.NotificationDeliveries.Where(x => x.Status == WorkStatus.DeadLetter)
            .OrderBy(x => x.DueUtc).ThenBy(x => x.Id).Take(100)
            .Select(x => new DeliveryFailure(x.Id, "delivery", "Email delivery failed. Check provider configuration and recipient availability.", x.Attempts, x.DueUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var outbox = await db.OutboxMessages.Where(x => x.Status == WorkStatus.DeadLetter)
            .OrderBy(x => x.DueUtc).ThenBy(x => x.Id).Take(100)
            .Select(x => new DeliveryFailure(x.Id, "outbox", "Change processing failed. Check schema compatibility and worker diagnostics.", x.Attempts, x.DueUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var scheduled = await db.ScheduledWork.Where(x => x.Status == WorkStatus.DeadLetter)
            .OrderBy(x => x.DueUtc).ThenBy(x => x.Id).Take(100)
            .Select(x => new DeliveryFailure(x.Id, "scheduled", "Scheduled work failed. Correct the dependency before replay.", x.Attempts, x.DueUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return deliveries.Concat(outbox).Concat(scheduled).OrderBy(x => x.DueUtc).ThenBy(x => x.Id).ToArray();
    }

    /// <inheritdoc/>
    public async Task ReplayAsync(Guid deliveryId, string kind, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireAdministratorAsync(db, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        switch (kind)
        {
            case "delivery":
                var delivery = await db.NotificationDeliveries.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken).ConfigureAwait(false)
                    ?? throw Unavailable();
                RequireDeadLetter(delivery.Status, delivery.LeaseId);
                delivery.Status = WorkStatus.Pending;
                delivery.Attempts = 0;
                delivery.DueUtc = now;
                delivery.LeaseUntilUtc = null;
                break;
            case "outbox":
                var outbox = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken).ConfigureAwait(false)
                    ?? throw Unavailable();
                RequireDeadLetter(outbox.Status, outbox.LeaseId);
                outbox.Status = WorkStatus.Pending;
                outbox.Attempts = 0;
                outbox.DueUtc = now;
                outbox.LeaseUntilUtc = null;
                break;
            case "scheduled":
                var scheduled = await db.ScheduledWork.SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken).ConfigureAwait(false)
                    ?? throw Unavailable();
                RequireDeadLetter(scheduled.Status, scheduled.LeaseId);
                scheduled.Status = WorkStatus.Pending;
                scheduled.Attempts = 0;
                scheduled.DueUtc = now;
                scheduled.LeaseUntilUtc = null;
                break;
            default:
                throw new DomainException(ErrorCode.Validation, "Unsupported replay category.", "kind");
        }
        Audit(db, actor.Id, deliveryId, "notification.work.replayed");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Audit(ISidequestDbContext db, Guid actorId, Guid resourceId, string action) =>
        db.AuditEntries.Add(new AuditEntry
        {
            ActorId = actorId, ResourceId = resourceId, ResourceKind = ResourceKind.System,
            Action = action, Reason = "Recipient delivery configuration or authorized recovery.",
            CorrelationId = Guid.NewGuid().ToString("N"), OccurredUtc = clock.GetUtcNow()
        });

    private static void RequireDeadLetter(WorkStatus status, Guid? lease)
    {
        if (status != WorkStatus.DeadLetter || lease is not null)
            throw new DomainException(ErrorCode.Conflict, "Only unclaimed failed work can be replayed.");
    }

    private static DomainException Unavailable() => new(ErrorCode.NotFound, "This resource is unavailable.");

    private static DomainException ScopeChanged() => new(ErrorCode.Conflict, "Your notification scope changed. Reload and try again.");

    private static async Task<Guid[]> JoinedEventIdsAsync(ISidequestDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var ids = await db.Quests.AsNoTracking().Where(q => db.Participations.Any(p => p.QuestId == q.Id &&
            p.UserId == userId && p.Status == ParticipationStatus.Joined)).Select(q => q.EventId)
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return ids.Order().ToArray();
    }

    private static async Task<Guid[]> NotificationEventIdsAsync(ISidequestDbContext db, Guid userId,
        Guid? notificationId, CancellationToken cancellationToken)
    {
        var ids = await db.Notifications.AsNoTracking().Where(x => x.UserId == userId && x.EventId != null &&
            (notificationId == null || x.Id == notificationId)).Select(x => x.EventId!.Value)
            .Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return ids.Order().ToArray();
    }

    private static async Task LockEventsAsync(ISidequestDbContext db, IEnumerable<Guid> eventIds, CancellationToken cancellationToken)
    {
        foreach (var eventId in eventIds)
            await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
    }
}
