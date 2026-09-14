using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Security;

public sealed class ResourceAccess(ICurrentUser currentUser) : IResourceAccess
{
    public async Task<UserAccount> RequireUserAsync(ISidequestDbContext db, CancellationToken cancellationToken = default)
    {
        var identity = await currentUser.GetIdentityAsync(cancellationToken)
            ?? throw new DomainException(ErrorCode.Forbidden, "Sign in to continue.");
        return await db.Users.SingleOrDefaultAsync(
            x => x.TenantId == identity.TenantId && x.ObjectId == identity.ObjectId &&
                x.IsEligible && x.DepartureVerifiedUtc == null, cancellationToken)
            ?? throw new DomainException(ErrorCode.Forbidden, "Your account is not eligible for Sidequest.");
    }

    public async Task<UserAccount> RequireAdministratorAsync(ISidequestDbContext db, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(db, cancellationToken);
        if (!await db.Administrators.AnyAsync(x => x.UserId == user.Id, cancellationToken))
            throw new DomainException(ErrorCode.Forbidden, "Administrator access is required.");
        return user;
    }

    public async Task<Event> RequireEventAsync(ISidequestDbContext db, Guid eventId, Guid userId,
        bool ownerOnly = false, CancellationToken cancellationToken = default)
    {
        var current = await RequireUserAsync(db, cancellationToken);
        if (current.Id != userId)
            throw Unavailable();
        var eligible = current.IsEligible;
        var item = await db.Events.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        var owner = await db.EventOwners.AnyAsync(x => x.EventId == eventId && x.UserId == userId, cancellationToken);
        var member = await db.EventMemberships.AnyAsync(
            x => x.EventId == eventId && x.UserId == userId && x.Status == MembershipStatus.Active, cancellationToken);
        if (item is null || !AccessRules.CanReadEvent(eligible, member, owner, item.Status) || (ownerOnly && !owner))
            throw Unavailable();
        return item;
    }

    public async Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid questId, Guid userId,
        bool ownerOnly = false, bool moderation = false, CancellationToken cancellationToken = default)
    {
        var item = await db.Quests.SingleOrDefaultAsync(x => x.Id == questId, cancellationToken);
        if (item is null)
            throw Unavailable();
        var parent = await RequireEventAsync(db, item.EventId, userId, cancellationToken: cancellationToken);
        var owner = await db.QuestOwners.AnyAsync(x => x.QuestId == questId && x.UserId == userId, cancellationToken);
        var invited = await db.QuestInvitations.AnyAsync(
            x => x.QuestId == questId && x.UserId == userId && x.Status == QuestInvitationStatus.Active, cancellationToken);
        var eventOwner = moderation && await db.EventOwners.AnyAsync(
            x => x.EventId == parent.Id && x.UserId == userId, cancellationToken);
        var allowed = moderation
            ? AccessRules.CanModerate(true, true, eventOwner, item.Status)
            : AccessRules.CanReadQuest(true, true, owner, invited, parent.Status, item.Status, item.Visibility);
        if (!allowed || (ownerOnly && !owner))
            throw Unavailable();
        return item;
    }

    private static DomainException Unavailable() => new(ErrorCode.NotFound, "This resource is unavailable.");
}
