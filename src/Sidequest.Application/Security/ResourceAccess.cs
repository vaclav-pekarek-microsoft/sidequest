using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Security;

/// <summary>Database-backed authorization using the current external identity and individual membership/ownership relations.</summary>
/// <param name="currentUser">Identity source; identity claims are rechecked against eligible, non-departed database accounts.</param>
/// <remarks>Authorization awaits do not capture a synchronization context. Calls sharing an EF context must remain sequential;
/// this scoped service does not make the supplied context or identity source thread-safe.</remarks>
public sealed class ResourceAccess(ICurrentUser currentUser) : IResourceAccess
{
    /// <inheritdoc/>
    public async Task<UserAccount> RequireUserAsync(ISidequestDbContext db, CancellationToken cancellationToken = default)
    {
        var identity = await currentUser.GetIdentityAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCode.Forbidden, "Sign in to continue.");
        return await db.Users.SingleOrDefaultAsync(
            x => x.TenantId == identity.TenantId && x.ObjectId == identity.ObjectId &&
                x.IsEligible && x.DepartureVerifiedUtc == null, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(ErrorCode.Forbidden, "Your account is not eligible for Sidequest.");
    }

    /// <inheritdoc/>
    public async Task<UserAccount> RequireAdministratorAsync(ISidequestDbContext db, CancellationToken cancellationToken = default)
    {
        var user = await RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        if (!await db.Administrators.AnyAsync(x => x.UserId == user.Id, cancellationToken).ConfigureAwait(false))
            throw new DomainException(ErrorCode.Forbidden, "Administrator access is required.");
        return user;
    }

    /// <inheritdoc/>
    public async Task<Event> RequireEventAsync(ISidequestDbContext db, Guid eventId, Guid userId,
        bool ownerOnly = false, CancellationToken cancellationToken = default)
    {
        var current = await RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        if (current.Id != userId)
            throw Unavailable();
        var eligible = current.IsEligible;
        var item = await db.Events.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken).ConfigureAwait(false);
        var owner = await db.EventOwners.AnyAsync(x => x.EventId == eventId && x.UserId == userId, cancellationToken).ConfigureAwait(false);
        var member = await db.EventMemberships.AnyAsync(
            x => x.EventId == eventId && x.UserId == userId && x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false);
        if (item is null || !AccessRules.CanReadEvent(eligible, member, owner, item.Status) || (ownerOnly && !owner))
            throw Unavailable();
        if (!owner && await db.EventStatusHistory.AnyAsync(x => x.EventId == eventId &&
            x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled, cancellationToken).ConfigureAwait(false))
            throw Unavailable();
        return item;
    }

    /// <inheritdoc/>
    public async Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid questId, Guid userId,
        bool ownerOnly = false, bool moderation = false, CancellationToken cancellationToken = default)
    {
        var item = await db.Quests.SingleOrDefaultAsync(x => x.Id == questId, cancellationToken).ConfigureAwait(false);
        if (item is null)
            throw Unavailable();
        var parent = await RequireEventAsync(db, item.EventId, userId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var owner = await db.QuestOwners.AnyAsync(x => x.QuestId == questId && x.UserId == userId, cancellationToken).ConfigureAwait(false);
        if ((moderation || !owner) && await db.QuestStatusHistory.AnyAsync(x => x.QuestId == questId &&
            x.Previous == QuestStatus.Draft && x.Next == QuestStatus.Cancelled, cancellationToken).ConfigureAwait(false))
            throw Unavailable();
        var invited = await db.QuestInvitations.AnyAsync(
            x => x.QuestId == questId && x.UserId == userId && x.Status == QuestInvitationStatus.Active, cancellationToken).ConfigureAwait(false);
        var eventOwner = moderation && await db.EventOwners.AnyAsync(
            x => x.EventId == parent.Id && x.UserId == userId, cancellationToken).ConfigureAwait(false);
        var allowed = moderation
            ? AccessRules.CanModerate(true, true, eventOwner, item.Status)
            : AccessRules.CanReadQuest(true, true, owner, invited, parent.Status, item.Status, item.Visibility);
        if (!allowed || (ownerOnly && !owner))
            throw Unavailable();
        return item;
    }

    private static DomainException Unavailable() => new(ErrorCode.NotFound, "This resource is unavailable.");
}
