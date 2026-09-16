using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Quests.Implementation;

internal static class QuestChanges
{
    internal static Task<Guid?> ParentIdAsync(ISidequestDbContext db, Guid questId, CancellationToken token) =>
        db.Quests.Where(q => q.Id == questId).Select(q => (Guid?)q.EventId).SingleOrDefaultAsync(token);

    internal static async Task<QuestAudience> CaptureAsync(ISidequestDbContext db, Guid id, CancellationToken token)
    {
        var owners = await db.QuestOwners.Where(x => x.QuestId == id).Select(x => x.UserId).ToArrayAsync(token).ConfigureAwait(false);
        var attendees = await db.Participations.Where(x => x.QuestId == id && x.Status == ParticipationStatus.Joined)
            .Select(x => x.UserId).ToArrayAsync(token).ConfigureAwait(false);
        var followers = await db.Participations.Where(x => x.QuestId == id && x.Status == ParticipationStatus.Following)
            .Select(x => x.UserId).ToArrayAsync(token).ConfigureAwait(false);
        var invitees = await db.QuestInvitations.Where(x => x.QuestId == id && x.Status == QuestInvitationStatus.Active)
            .Select(x => x.UserId).ToArrayAsync(token).ConfigureAwait(false);
        return new(owners, attendees, followers, invitees);
    }

    internal static Guid Audit(ISidequestDbContext db, Quest quest, Guid? actor, string action, string reason,
        DateTimeOffset now)
    {
        var changeId = Guid.NewGuid();
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceKind = ResourceKind.Quest,
            ResourceId = quest.Id,
            ActorId = actor,
            Action = action,
            Reason = reason,
            OccurredUtc = now,
            CorrelationId = changeId.ToString("N")
        });
        quest.UpdatedUtc = now;
        return changeId;
    }

    internal static void Notify(ISidequestDbContext db, IChangeWriter writer, Quest quest, Guid changeId,
        Guid? actor, NotificationKind kind, IEnumerable<Guid> recipients, DateTimeOffset now, string reason = "",
        Guid[]? previousAttendees = null, bool calendarChanged = false, bool material = false,
        Guid[]? affectedUsers = null) =>
        writer.Append(db, new ChangeEnvelope(changeId, kind, quest.EventId, quest.Id, actor,
            recipients.Distinct().ToArray(), now, quest.CalendarRevision, reason, previousAttendees,
            calendarChanged, material, affectedUsers));

    internal static async Task ScheduleAsync(ISidequestDbContext db, Quest quest, CancellationToken token)
    {
        var key = $"quest-complete:{quest.Id:N}:{quest.EndUtc.UtcTicks}";
        if (!await db.HasPendingScheduledWorkForUpdateAsync(key, token).ConfigureAwait(false))
            db.ScheduledWork.Add(new ScheduledWork
            {
                Type = WorkTypes.QuestCompletion,
                QuestId = quest.Id,
                DeduplicationKey = $"{key}:{Guid.NewGuid():N}",
                DueUtc = quest.EndUtc,
                PayloadJson = JsonSerializer.Serialize(new QuestCompletionPayload(quest.Id, quest.EndUtc))
            });
    }

    internal static async Task TransitionAsync(ISidequestDbContext db, IChangeWriter writer, Quest quest,
        QuestStatus next, Guid? actor, string reason, DateTimeOffset now, CancellationToken token,
        bool suppressDelivery = false, bool parentCancellation = false)
    {
        if (quest.Status == next)
            return;
        var previous = quest.Status;
        // Draft publication has no participation audience; public fan-out is resolved
        // by the outbox handler. Reading unused audiences can deadlock a concurrent Join.
        var audience = previous == QuestStatus.Draft && next == QuestStatus.Active
            ? new QuestAudience([], [], [], [])
            : await CaptureAsync(db, quest.Id, token).ConfigureAwait(false);
        quest.Status = next;
        quest.StatusReason = reason;
        var changeId = Audit(db, quest, actor, $"Status:{previous}->{next}", reason, now);
        db.QuestStatusHistory.Add(new QuestStatusHistory
        {
            QuestId = quest.Id,
            Previous = previous,
            Next = next,
            ActorId = actor,
            Reason = reason,
            OccurredUtc = now
        });
        NotificationKind? kind = (previous, next) switch
        {
            (QuestStatus.Draft, QuestStatus.Active) when quest.Visibility == QuestVisibility.Public => NotificationKind.QuestPublished,
            (QuestStatus.Active, QuestStatus.Suspended) => NotificationKind.QuestSuspended,
            (QuestStatus.Suspended, QuestStatus.Active) => NotificationKind.QuestReinstated,
            (QuestStatus.Active or QuestStatus.Suspended, QuestStatus.Cancelled) =>
                parentCancellation ? NotificationKind.EventCancelled : NotificationKind.QuestCancelled,
            _ => null
        };
        var calendar = next is QuestStatus.Suspended or QuestStatus.Cancelled && previous != QuestStatus.Draft ||
            previous == QuestStatus.Suspended && next == QuestStatus.Active;
        if (calendar)
            quest.CalendarRevision++;
        // The parent coalesces status mail; child EventCancelled envelopes carry only attendee withdrawals.
        if (!suppressDelivery && kind is not null &&
            (kind != NotificationKind.EventCancelled || audience.Attendees.Length != 0))
            Notify(db, writer, quest, changeId, actor, kind.Value,
                kind switch
                {
                    NotificationKind.QuestPublished => [],
                    NotificationKind.EventCancelled => audience.Attendees,
                    _ => audience.All
                },
                now, reason, audience.Attendees, calendar, calendar);
        if (next == QuestStatus.Active)
            await ScheduleAsync(db, quest, token).ConfigureAwait(false);
    }

    internal static async Task WithdrawAsync(ISidequestDbContext db, IChangeWriter writer, Quest quest,
        Guid userId, Guid? actor, string action, string reason, NotificationKind kind, DateTimeOffset now,
        CancellationToken token, bool revokeInvitation, bool joinedOnly = false)
    {
        var invitation = revokeInvitation
            ? await db.QuestInvitations.SingleOrDefaultAsync(x => x.QuestId == quest.Id && x.UserId == userId, token).ConfigureAwait(false)
            : null;
        var participation = await db.Participations.SingleOrDefaultAsync(
            x => x.QuestId == quest.Id && x.UserId == userId, token).ConfigureAwait(false);
        var grantChanged = invitation?.Status == QuestInvitationStatus.Active;
        var previous = participation?.Status ?? ParticipationStatus.None;
        var participantChanged = previous != ParticipationStatus.None && (!joinedOnly || previous == ParticipationStatus.Joined);
        if (!grantChanged && !participantChanged)
            return;
        var audience = await CaptureAsync(db, quest.Id, token).ConfigureAwait(false);
        if (grantChanged)
        {
            invitation!.Status = QuestInvitationStatus.Revoked;
            invitation.ChangedUtc = now;
        }
        if (participantChanged)
        {
            participation!.Status = ParticipationStatus.None;
            participation.ChangedUtc = now;
        }
        var wasJoined = participantChanged && previous == ParticipationStatus.Joined;
        if (wasJoined)
            quest.CalendarRevision++;
        var change = Audit(db, quest, actor, $"{action}:{userId:N}:{previous}->None", reason, now);
        Notify(db, writer, quest, change, actor, kind, wasJoined ? audience.Owners.Append(userId) : [userId], now, reason,
            wasJoined ? [userId] : [], wasJoined, true, [userId]);
    }

    internal static void RequireActive(Event parent, DateTimeOffset now)
    {
        if (parent.Status != EventStatus.Active || ParentIsOverdue(parent, now))
            throw new DomainException(ErrorCode.Conflict, "The Event is not active.");
    }

    internal static bool ParentIsOverdue(Event parent, DateTimeOffset now) =>
        parent.Status == EventStatus.Active &&
        now >= TimeRules.EventWindow(parent.StartDate, parent.EndDate, parent.TimeZoneId).End;

    internal static QuestStatus EffectiveStatus(Quest quest, Event parent, DateTimeOffset now) =>
        quest.Status is QuestStatus.Active or QuestStatus.Suspended &&
        (now >= quest.EndUtc || parent.Status == EventStatus.Completed ||
            now >= TimeRules.EventWindow(parent.StartDate, parent.EndDate, parent.TimeZoneId).End)
            ? QuestStatus.Completed : quest.Status;
}
