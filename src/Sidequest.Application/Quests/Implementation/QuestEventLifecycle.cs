using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Quests.Implementation;

/// <summary>Stages child lifecycle and access-loss effects inside an already-authorized Event transaction.</summary>
/// <param name="writer">Stages versioned changes without contacting delivery providers.</param>
public sealed class QuestEventLifecycle(IChangeWriter writer) : IQuestEventLifecycle
{
    /// <inheritdoc />
    /// <remarks>Already-ended published children are reconciled as Completed without withdrawing historical calendars.
    /// Future published children stage attendee-only EventCancelled withdrawal envelopes. The Event caller owns
    /// the coalesced status notice and must capture its affected audience separately.</remarks>
    public async Task CancelForEventAsync(ISidequestDbContext db, Guid eventId, Guid? actorId, string reason,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var children = await db.Quests.Where(x => x.EventId == eventId &&
            (x.Status == QuestStatus.Draft || x.Status == QuestStatus.Active || x.Status == QuestStatus.Suspended))
            .OrderBy(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var quest in children)
        {
            if (quest.Status is not (QuestStatus.Draft or QuestStatus.Active or QuestStatus.Suspended))
                continue;
            if (quest.Status != QuestStatus.Draft && quest.EndUtc <= now)
                await QuestChanges.TransitionAsync(db, writer, quest, QuestStatus.Completed, null,
                    "The Quest end time has been reached.", now, cancellationToken).ConfigureAwait(false);
            else
                await QuestChanges.TransitionAsync(db, writer, quest, QuestStatus.Cancelled, actorId, reason, now,
                    cancellationToken, parentCancellation: true).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task CompleteForEventAsync(ISidequestDbContext db, Guid eventId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var children = await db.Quests.Where(x => x.EventId == eventId &&
            (x.Status == QuestStatus.Draft ||
             (x.Status == QuestStatus.Active || x.Status == QuestStatus.Suspended) && x.EndUtc <= now))
            .OrderBy(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var quest in children)
        {
            if (quest.Status is not (QuestStatus.Draft or QuestStatus.Active or QuestStatus.Suspended))
                continue;
            await QuestChanges.TransitionAsync(db, writer, quest,
                quest.Status == QuestStatus.Draft ? QuestStatus.Cancelled : QuestStatus.Completed,
                null, "The parent Event has completed.", now, cancellationToken, suppressDelivery: true).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task RemoveMemberParticipationAsync(ISidequestDbContext db, Guid eventId, Guid userId,
        Guid actorId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var children = await db.Quests.Where(x => x.EventId == eventId &&
            (db.Participations.Any(p => p.QuestId == x.Id && p.UserId == userId && p.Status != ParticipationStatus.None) ||
             db.QuestInvitations.Any(i => i.QuestId == x.Id && i.UserId == userId && i.Status == QuestInvitationStatus.Active)))
            .OrderBy(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var quest in children)
            await QuestChanges.WithdrawAsync(db, writer, quest, userId, actorId, "MembershipLost", reason,
                NotificationKind.AccessRemoved, now, cancellationToken, revokeInvitation: true).ConfigureAwait(false);
    }
}
