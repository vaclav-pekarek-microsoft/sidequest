using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Notifications.Implementation;

/// <summary>Recipient authorization over persisted facts, shared by workers and recipient queries without impersonating a UI identity.</summary>
public sealed class RecipientPolicy
{
    /// <summary>Builds a SQL-filtered current recipient inbox, excluding inaccessible protected records before paging/counting.</summary>
    /// <param name="db">Operation-scoped context.</param>
    /// <param name="userId">Already authenticated eligible recipient.</param>
    /// <param name="now">Current UTC instant used to hide expired invitation notifications.</param>
    /// <returns>Filtered query; generic service notices remain readable without resource links.</returns>
    public IQueryable<Notification> Visible(ISidequestDbContext db, Guid userId, DateTimeOffset now) =>
        db.Notifications.Where(n => n.UserId == userId &&
            db.Users.Any(u => u.Id == userId && u.IsEligible && u.DepartureVerifiedUtc == null) &&
            (!db.EventStatusHistory.Any(h => h.EventId == n.EventId && h.Previous == EventStatus.Draft && h.Next == EventStatus.Cancelled) ||
                db.EventOwners.Any(o => o.EventId == n.EventId && o.UserId == userId)) &&
            (n.Kind != NotificationKind.MembershipRequested ||
                db.EventOwners.Any(o => o.EventId == n.EventId && o.UserId == userId)) &&
            (n.Kind != NotificationKind.SuspendedQuestEdited ||
                (!db.QuestStatusHistory.Any(h => h.QuestId == n.QuestId && h.Previous == QuestStatus.Draft && h.Next == QuestStatus.Cancelled) &&
                 (db.EventOwners.Any(o => o.EventId == n.EventId && o.UserId == userId) ||
                  db.QuestOwners.Any(o => o.QuestId == n.QuestId && o.UserId == userId)))) &&
            (n.Kind != NotificationKind.EventInvitation ||
                db.EventInvitations.Any(i => i.EventId == n.EventId && i.UserId == userId &&
                    i.Status == EventInvitationStatus.Pending && i.ExpiresUtc > now)) &&
            (n.IsAccessLossNotice ||
             (n.EventId != null &&
              db.EventMemberships.Any(m => m.EventId == n.EventId && m.UserId == userId && m.Status == MembershipStatus.Active) &&
              db.Events.Any(e => e.Id == n.EventId && (e.Status != EventStatus.Draft ||
                  db.EventOwners.Any(o => o.EventId == e.Id && o.UserId == userId))) &&
              (n.QuestId == null || db.Quests.Any(q => q.Id == n.QuestId && q.EventId == n.EventId &&
                  (q.Status == QuestStatus.Draft || db.QuestStatusHistory.Any(h => h.QuestId == q.Id &&
                      h.Previous == QuestStatus.Draft && h.Next == QuestStatus.Cancelled)
                      ? db.QuestOwners.Any(o => o.QuestId == q.Id && o.UserId == userId)
                      : q.Visibility == QuestVisibility.Public ||
                        db.QuestOwners.Any(o => o.QuestId == q.Id && o.UserId == userId) ||
                        db.QuestInvitations.Any(i => i.QuestId == q.Id && i.UserId == userId && i.Status == QuestInvitationStatus.Active)))))));

    /// <summary>Checks ordinary current Quest disclosure using individual membership, invitations, and ownership.</summary>
    /// <param name="db">Operation-scoped context.</param>
    /// <param name="quest">Persisted Quest.</param>
    /// <param name="userId">Captured recipient, not a current-user override.</param>
    /// <param name="cancellationToken">Cancels fact retrieval.</param>
    /// <returns>True only with currently eligible ordinary content access.</returns>
    public async Task<bool> CanReadQuestAsync(ISidequestDbContext db, Quest quest, Guid userId, CancellationToken cancellationToken)
    {
        var parent = await db.Events.SingleOrDefaultAsync(x => x.Id == quest.EventId, cancellationToken).ConfigureAwait(false);
        if (parent is null || !await AllowsEventDisclosureAsync(db, parent.Id, userId, cancellationToken).ConfigureAwait(false))
            return false;
        var eligible = await EligibleAsync(db, userId, cancellationToken).ConfigureAwait(false);
        var member = await db.EventMemberships.AnyAsync(x => x.EventId == parent.Id && x.UserId == userId &&
            x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false);
        var owner = await db.QuestOwners.AnyAsync(x => x.QuestId == quest.Id && x.UserId == userId, cancellationToken).ConfigureAwait(false);
        var invited = await db.QuestInvitations.AnyAsync(x => x.QuestId == quest.Id && x.UserId == userId &&
            x.Status == QuestInvitationStatus.Active, cancellationToken).ConfigureAwait(false);
        var unpublishedCancellation = await db.QuestStatusHistory.AnyAsync(x => x.QuestId == quest.Id &&
            x.Previous == QuestStatus.Draft && x.Next == QuestStatus.Cancelled, cancellationToken).ConfigureAwait(false);
        return AccessRules.CanReadQuest(eligible, member, owner, invited, parent.Status,
            unpublishedCancellation ? QuestStatus.Draft : quest.Status, quest.Visibility);
    }

    /// <summary>Checks account eligibility independently of historical recipient intent.</summary>
    /// <param name="db">Operation-scoped context.</param>
    /// <param name="userId">Recipient account identifier.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>True for an eligible account with no verified departure.</returns>
    public Task<bool> EligibleAsync(ISidequestDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(x => x.Id == userId && x.IsEligible && x.DepartureVerifiedUtc == null, cancellationToken);

    /// <summary>Applies the additional Event publication boundary, retaining owner-only disclosure after an unpublished cancellation or archive.</summary>
    /// <param name="db">Operation-scoped context.</param>
    /// <param name="eventId">Event whose retained history controls publication.</param>
    /// <param name="userId">Recipient whose explicit Event ownership may permit unpublished disclosure.</param>
    /// <param name="cancellationToken">Cancels persisted fact retrieval.</param>
    /// <returns>Whether the publication boundary permits disclosure; this does not grant eligibility, membership or Quest access.</returns>
    public Task<bool> AllowsEventDisclosureAsync(ISidequestDbContext db, Guid eventId, Guid userId, CancellationToken cancellationToken) =>
        db.Events.AnyAsync(e => e.Id == eventId &&
            ((e.Status != EventStatus.Draft &&
              !db.EventStatusHistory.Any(h => h.EventId == e.Id && h.Previous == EventStatus.Draft && h.Next == EventStatus.Cancelled)) ||
             db.EventOwners.Any(o => o.EventId == e.Id && o.UserId == userId)), cancellationToken);

    /// <summary>Checks source-specific current intent without allowing a captured recipient list to restore access.</summary>
    /// <param name="db">Operation-scoped context.</param>
    /// <param name="change">Captured source event.</param>
    /// <param name="userId">Recipient account.</param>
    /// <param name="now">Current UTC instant.</param>
    /// <param name="cancellationToken">Cancels reads.</param>
    /// <returns>Whether a generic notification is still appropriate; protected details require a separate content check.</returns>
    public async Task<bool> CanReceiveAsync(ISidequestDbContext db, ChangeEnvelope change, Guid userId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await EligibleAsync(db, userId, cancellationToken).ConfigureAwait(false) ||
            !await AllowsEventDisclosureAsync(db, change.EventId, userId, cancellationToken).ConfigureAwait(false))
            return false;
        if (change.Kind is NotificationKind.AccessRemoved or NotificationKind.Left or NotificationKind.AttendeeRemoved
            or NotificationKind.MembershipDecided or NotificationKind.OwnershipChanged)
            return change.RecipientIds.Contains(userId);
        if (change.Kind == NotificationKind.EventInvitation)
            return await db.EventInvitations.AnyAsync(x => x.EventId == change.EventId && x.UserId == userId &&
                x.Status == EventInvitationStatus.Pending && x.ExpiresUtc > now &&
                db.Events.Any(e => e.Id == x.EventId && e.Status == EventStatus.Active), cancellationToken).ConfigureAwait(false);
        var member = await db.EventMemberships.AnyAsync(x => x.EventId == change.EventId && x.UserId == userId &&
            x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false);
        if (!member)
            return false;
        if (change.Kind is NotificationKind.MembershipRequested or NotificationKind.BulkCompleted)
            return await db.EventOwners.AnyAsync(x => x.EventId == change.EventId && x.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (change.QuestId is not Guid questId)
            return true;
        var quest = await db.Quests.SingleOrDefaultAsync(x => x.Id == questId, cancellationToken).ConfigureAwait(false);
        if (quest is null)
            return false;
        if (change.Kind == NotificationKind.SuspendedQuestEdited)
            return !await db.QuestStatusHistory.AnyAsync(x => x.QuestId == questId &&
                x.Previous == QuestStatus.Draft && x.Next == QuestStatus.Cancelled, cancellationToken).ConfigureAwait(false) &&
                (await db.QuestOwners.AnyAsync(x => x.QuestId == questId && x.UserId == userId, cancellationToken).ConfigureAwait(false)
                 || await db.EventOwners.AnyAsync(x => x.EventId == quest.EventId && x.UserId == userId, cancellationToken).ConfigureAwait(false));
        if (!await CanReadQuestAsync(db, quest, userId, cancellationToken).ConfigureAwait(false))
            return false;
        if (change.Kind == NotificationKind.QuestPublished)
            return quest.Visibility == QuestVisibility.Public && quest.Status == QuestStatus.Active && quest.EndUtc > now &&
                await db.Events.AnyAsync(x => x.Id == quest.EventId && x.Status == EventStatus.Active, cancellationToken).ConfigureAwait(false);
        if (change.Kind == NotificationKind.QuestInvitation)
            return quest.Status == QuestStatus.Active && quest.EndUtc > now &&
                await db.QuestInvitations.AnyAsync(x => x.QuestId == quest.Id && x.UserId == userId &&
                    x.Status == QuestInvitationStatus.Active, cancellationToken).ConfigureAwait(false);
        if (change.Kind == NotificationKind.QuestSuspended && quest.Status == QuestStatus.Active)
            return false;
        if (change.Kind == NotificationKind.QuestReinstated && quest.Status != QuestStatus.Active)
            return false;
        if (change.Kind is NotificationKind.Reminder or NotificationKind.Joined)
            return await db.Participations.AnyAsync(x => x.QuestId == questId && x.UserId == userId &&
                x.Status == ParticipationStatus.Joined, cancellationToken).ConfigureAwait(false)
                || (change.Kind == NotificationKind.Joined &&
                    await db.QuestOwners.AnyAsync(x => x.QuestId == questId && x.UserId == userId, cancellationToken).ConfigureAwait(false));
        if (change.Kind == NotificationKind.QuestUpdated)
            return quest.Status == QuestStatus.Active && quest.EndUtc > now &&
                (await db.Participations.AnyAsync(x => x.QuestId == questId && x.UserId == userId &&
                    x.Status != ParticipationStatus.None, cancellationToken).ConfigureAwait(false)
                 || await db.QuestOwners.AnyAsync(x => x.QuestId == questId && x.UserId == userId, cancellationToken).ConfigureAwait(false));
        return true;
    }
}
