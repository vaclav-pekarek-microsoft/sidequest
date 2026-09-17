using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Events.Implementation;

internal static class EventTransactions
{
    internal static DomainException Unavailable() => new(ErrorCode.NotFound, "This Event or membership item is unavailable.");

    internal static DomainException Conflict(string message) => new(ErrorCode.Conflict, message);

    internal static async Task<Event> LockAsync(ISidequestDbContext db, Guid eventId, CancellationToken cancellationToken)
    {
        await db.LockEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        return await db.Events.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken).ConfigureAwait(false)
            ?? throw Unavailable();
    }

    internal static DateTimeOffset End(Event item) =>
        TimeRules.EventWindow(item.StartDate, item.EndDate, item.TimeZoneId).End.ToUniversalTime();

    internal static EventStatus Effective(Event item, DateTimeOffset now) =>
        item.Status == EventStatus.Active && End(item) <= now ? EventStatus.Completed : item.Status;

    internal static void RequireActive(Event item, DateTimeOffset now)
    {
        if (Effective(item, now) != EventStatus.Active)
            throw Conflict("This Event is not accepting new activity.");
    }

    internal static void RequireEditable(Event item, DateTimeOffset now)
    {
        if (item.Status is not (EventStatus.Draft or EventStatus.Active) || End(item) <= now)
            throw Conflict("Only a Draft or Active Event that has not ended can be edited.");
    }

    internal static Guid Audit(ISidequestDbContext db, Guid eventId, Guid? actorId,
        string action, string reason, DateTimeOffset now)
    {
        var changeId = Guid.NewGuid();
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceId = eventId, ResourceKind = ResourceKind.Event, ActorId = actorId,
            Action = action, Reason = reason, OccurredUtc = now, CorrelationId = changeId.ToString("N")
        });
        return changeId;
    }

    internal static async Task ClosePendingAsync(ISidequestDbContext db, Event item, Guid? actorId,
        DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var invitations = await db.EventInvitations.Where(x => x.EventId == item.Id &&
            x.Status == EventInvitationStatus.Pending).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var invitation in invitations)
        {
            invitation.Status = EventInvitationStatus.Expired;
            invitation.ResolvedUtc = now;
        }
        var requests = await db.MembershipRequests.Where(x => x.EventId == item.Id &&
            x.Status == MembershipRequestStatus.Pending).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var request in requests)
        {
            request.Status = MembershipRequestStatus.Rejected;
            request.DecidedById = actorId;
            request.DecidedUtc = now;
            request.Reason = reason;
        }
    }

    internal static void Transition(ISidequestDbContext db, Event item, EventStatus next,
        Guid? actorId, string reason, DateTimeOffset now)
    {
        db.EventStatusHistory.Add(new EventStatusHistory
        {
            EventId = item.Id, Previous = item.Status, Next = next, ActorId = actorId,
            Reason = reason, OccurredUtc = now
        });
        item.Status = next;
        item.UpdatedUtc = now;
        Audit(db, item.Id, actorId, $"Event.{next}", reason, now);
    }

    internal static async Task<bool> CompleteAsync(ISidequestDbContext db, Event item,
        IQuestEventLifecycle quests, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (item.Status != EventStatus.Active || End(item) > now)
            return false;
        const string reason = "The Event reached its inclusive local end date.";
        await quests.CompleteForEventAsync(db, item.Id, now, cancellationToken).ConfigureAwait(false);
        await ClosePendingAsync(db, item, null, now, reason, cancellationToken).ConfigureAwait(false);
        Transition(db, item, EventStatus.Completed, null, reason, now);
        return true;
    }

    internal static async Task ScheduleCompletionAsync(ISidequestDbContext db, Event item,
        CancellationToken cancellationToken)
    {
        var end = End(item);
        var key = $"event.complete.v1:{item.Id:N}:{end.UtcTicks}";
        if (await db.HasScheduledWorkForUpdateAsync(key, cancellationToken).ConfigureAwait(false))
            return;
        db.ScheduledWork.Add(new ScheduledWork
        {
            Type = WorkTypes.EventCompletion, DeduplicationKey = key, DueUtc = end,
            PayloadJson = JsonSerializer.Serialize(new EventCompletionPayload(1, item.Id, end))
        });
    }

    internal static T ReadPayload<T>(ScheduledWork work, string expectedType)
    {
        if (work.Type != expectedType)
            throw new DomainException(ErrorCode.Validation, "Scheduled work has an incompatible type.");
        try
        {
            return JsonSerializer.Deserialize<T>(work.PayloadJson)
                ?? throw new DomainException(ErrorCode.Validation, "Scheduled work has an invalid payload.");
        }
        catch (JsonException)
        {
            throw new DomainException(ErrorCode.Validation, "Scheduled work has an invalid payload.");
        }
    }
}
