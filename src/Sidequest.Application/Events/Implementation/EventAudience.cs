using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Events.Implementation;

internal sealed class EventAudience(IChangeWriter changes, EventOperationOptions options)
{
    internal static async Task<UserAccount> ResolveLocalAsync(ISidequestDbContext db, DirectoryUser resolved,
        Guid tenantId, CancellationToken cancellationToken)
    {
        if (resolved.TenantId != tenantId || resolved.ObjectId == Guid.Empty || !resolved.IsEligible)
            throw new DomainException(ErrorCode.Validation, "Choose an eligible workforce account from this tenant.", "User");
        var user = await db.Users.SingleOrDefaultAsync(x => x.TenantId == resolved.TenantId &&
            x.ObjectId == resolved.ObjectId, cancellationToken).ConfigureAwait(false);
        if (user is not null && (!user.IsEligible || user.DepartureVerifiedUtc is not null))
            throw new DomainException(ErrorCode.Validation, "This account is not eligible.", "User");
        if (user is null)
        {
            user = new UserAccount { TenantId = resolved.TenantId, ObjectId = resolved.ObjectId, IsEligible = true };
            db.Users.Add(user);
        }
        user.DisplayName = InputRules.Text(resolved.DisplayName, "DisplayName", 1, 256);
        user.Email = InputRules.Text(resolved.Email, "Email", 0, 320);
        return user;
    }

    internal static async Task<UserAccount> EligibleAsync(ISidequestDbContext db, Guid id,
        Guid tenantId, CancellationToken cancellationToken) =>
        await db.Users.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId &&
            x.IsEligible && x.DepartureVerifiedUtc == null, cancellationToken).ConfigureAwait(false)
        ?? throw new DomainException(ErrorCode.Validation, "This account is not eligible.", "User");

    internal async Task<bool> ActivateAsync(ISidequestDbContext db, Event item, Guid userId,
        Guid actorId, bool restore, DateTimeOffset now, string reason, NotificationKind kind,
        CancellationToken cancellationToken)
    {
        var member = await db.EventMemberships.SingleOrDefaultAsync(x => x.EventId == item.Id &&
            x.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (member?.Status == MembershipStatus.Active)
            return false;
        if (member is not null && !restore)
            throw EventTransactions.Conflict("This membership was removed. Explicit individual restoration is required.");
        if (member is null)
        {
            member = new EventMembership { EventId = item.Id, UserId = userId };
            db.EventMemberships.Add(member);
        }
        member.Status = MembershipStatus.Active;
        member.ChangedById = actorId;
        member.ChangedUtc = now;
        await ResolvePendingAsync(db, item.Id, userId, actorId, now, reason, cancellationToken).ConfigureAwait(false);
        var changeId = EventTransactions.Audit(db, item.Id, actorId, "Membership.Activated",
            $"User {userId:N}: {reason}", now);
        if (item.Status != EventStatus.Draft)
        {
            var recipients = await OwnerIdsAsync(db, item.Id, cancellationToken).ConfigureAwait(false);
            changes.Append(db, new ChangeEnvelope(changeId, kind, item.Id, null, actorId,
                recipients.Append(userId).Distinct().ToArray(), now, Reason: reason, AffectedUserIds: [userId]));
        }
        return true;
    }

    internal static async Task ResolvePendingAsync(ISidequestDbContext db, Guid eventId, Guid userId,
        Guid actorId, DateTimeOffset now, string reason, CancellationToken cancellationToken)
    {
        var requests = await db.MembershipRequests.Where(x => x.EventId == eventId && x.UserId == userId &&
            x.Status == MembershipRequestStatus.Pending).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var request in requests)
        {
            request.Status = MembershipRequestStatus.Approved;
            request.DecidedById = actorId;
            request.DecidedUtc = now;
            request.Reason = reason;
        }
        var invitations = await db.EventInvitations.Where(x => x.EventId == eventId && x.UserId == userId &&
            x.Status == EventInvitationStatus.Pending).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var invitation in invitations)
        {
            invitation.Status = invitation.ExpiresUtc <= now ? EventInvitationStatus.Expired : EventInvitationStatus.Accepted;
            invitation.ResolvedUtc = now;
            EventTransactions.Audit(db, eventId, actorId, $"Invitation.{invitation.Status}",
                $"Invitation {invitation.Id:N}; user {userId:N}: {reason}", now);
        }
    }

    internal async Task<bool> InviteAsync(ISidequestDbContext db, Event item, Guid userId, Guid actorId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        EventTransactions.RequireActive(item, now);
        if (await db.EventMemberships.AnyAsync(x => x.EventId == item.Id && x.UserId == userId &&
            x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false))
            return false;
        var pending = await db.EventInvitations.SingleOrDefaultAsync(x => x.EventId == item.Id &&
            x.UserId == userId && x.Status == EventInvitationStatus.Pending, cancellationToken).ConfigureAwait(false);
        if (pending is not null)
        {
            if (pending.ExpiresUtc > now)
                return false;
            pending.Status = EventInvitationStatus.Expired;
            pending.ResolvedUtc = now;
            // Release the filtered unique key before inserting a fresh consent request.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        var since = now.AddHours(-1);
        if (await db.EventInvitations.CountAsync(x => x.InvitedById == actorId && x.CreatedUtc >= since,
            cancellationToken).ConfigureAwait(false) >= options.InvitationsPerHour)
            throw EventTransactions.Conflict("The invitation rate limit was reached. Try again later.");
        var end = EventTransactions.End(item);
        db.EventInvitations.Add(new EventInvitation
        {
            EventId = item.Id, UserId = userId, InvitedById = actorId, CreatedUtc = now,
            ExpiresUtc = end < now.AddDays(7) ? end : now.AddDays(7)
        });
        var changeId = EventTransactions.Audit(db, item.Id, actorId, "Invitation.Created", $"User {userId:N}", now);
        changes.Append(db, new ChangeEnvelope(changeId, NotificationKind.EventInvitation,
            item.Id, null, actorId, [userId], now));
        return true;
    }

    internal static Task<Guid[]> OwnerIdsAsync(ISidequestDbContext db, Guid eventId, CancellationToken cancellationToken) =>
        (from owner in db.EventOwners
         join user in db.Users on owner.UserId equals user.Id
         where owner.EventId == eventId && user.IsEligible && user.DepartureVerifiedUtc == null
         select user.Id).ToArrayAsync(cancellationToken);

    internal static Task<Guid[]> CancellationRecipientsAsync(ISidequestDbContext db, Guid eventId, Guid tenantId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var affectedQuests = db.Quests.Where(x => x.EventId == eventId && x.EndUtc > now &&
            (x.Status == QuestStatus.Active || x.Status == QuestStatus.Suspended)).Select(x => x.Id);
        var affectedUsers = db.QuestOwners.Where(x => affectedQuests.Contains(x.QuestId)).Select(x => x.UserId)
            .Union(db.QuestInvitations.Where(x => affectedQuests.Contains(x.QuestId) &&
                x.Status == QuestInvitationStatus.Active).Select(x => x.UserId))
            .Union(db.Participations.Where(x => affectedQuests.Contains(x.QuestId) &&
                (x.Status == ParticipationStatus.Following || x.Status == ParticipationStatus.Joined)).Select(x => x.UserId));
        return (from member in db.EventMemberships
                join user in db.Users on member.UserId equals user.Id
                where member.EventId == eventId && member.Status == MembershipStatus.Active &&
                    user.TenantId == tenantId && user.IsEligible && user.DepartureVerifiedUtc == null &&
                    (user.LastSignedInUtc != null || affectedUsers.Contains(user.Id))
                select user.Id).Distinct().ToArrayAsync(cancellationToken);
    }
}
