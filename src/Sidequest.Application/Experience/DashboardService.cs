using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Experience;

/// <summary>Composes existing authorized paging with one bounded bulk owner-statistics query, never detail-per-card reads.</summary>
/// <param name="quests">The authoritative Quest list and date-filter implementation.</param>
/// <param name="factory">Creates short-lived database contexts.</param>
/// <param name="access">Rechecks the current actor's persisted eligibility.</param>
public sealed class DashboardService(IQuestService quests, ISidequestDbContextFactory factory, IResourceAccess access)
{
    /// <summary>Loads one authorized page and eligible, active-member invitation counts for its currently owned private Quests.</summary>
    /// <param name="kind">Joined, Following, Organizing, Discover, Invited or History view.</param>
    /// <param name="eventId">Optional authorized parent filter.</param>
    /// <param name="page">One-based page, default size 25 and maximum 100, validated by the Quest service.</param>
    /// <param name="dates">Inclusive lower and exclusive upper UTC start bounds applied before paging.</param>
    /// <param name="cancellationToken">Cancels all service and SQL reads.</param>
    /// <returns>The original page and a separate, privacy-filtered count map; no invitation identities leave this service.</returns>
    /// <exception cref="Sidequest.Domain.Rules.DomainException">The existing query or current account authorization fails.</exception>
    public async Task<DashboardPage> ListAsync(QuestListKind kind, Guid? eventId, PageRequest page,
        QuestDateFilter dates, CancellationToken cancellationToken = default)
    {
        if (kind is not (QuestListKind.Joined or QuestListKind.Following or QuestListKind.Organizing or
            QuestListKind.Discover or QuestListKind.Invited or QuestListKind.History))
            throw new DomainException(ErrorCode.Validation, "Choose a dashboard view.", "Kind");
        var result = await quests.ListAsync(kind, eventId, page, dates, cancellationToken).ConfigureAwait(false);
        var ids = result.Items.Where(q => q.IsOwner && q.Visibility == QuestVisibility.Private)
            .Select(q => q.Id).ToArray();
        if (ids.Length == 0)
            return new(result, new Dictionary<Guid, int>());

        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, cancellationToken).ConfigureAwait(false);
        var counts = await db.Quests.AsNoTracking()
            .Where(q => ids.Contains(q.Id) && q.Visibility == QuestVisibility.Private &&
                db.QuestOwners.Any(o => o.QuestId == q.Id && o.UserId == actor.Id) &&
                db.EventMemberships.Any(m => m.EventId == q.EventId && m.UserId == actor.Id && m.Status == MembershipStatus.Active) &&
                db.Events.Any(e => e.Id == q.EventId &&
                    ((e.Status != EventStatus.Draft &&
                        !db.EventStatusHistory.Any(h => h.EventId == e.Id && h.Previous == EventStatus.Draft && h.Next == EventStatus.Cancelled)) ||
                     db.EventOwners.Any(o => o.EventId == e.Id && o.UserId == actor.Id))))
            .Select(q => new
            {
                q.Id,
                Count = db.QuestInvitations.Count(i => i.QuestId == q.Id && i.Status == QuestInvitationStatus.Active &&
                    db.Users.Any(u => u.Id == i.UserId && u.IsEligible && u.DepartureVerifiedUtc == null) &&
                    db.EventMemberships.Any(m => m.EventId == q.EventId && m.UserId == i.UserId && m.Status == MembershipStatus.Active))
            })
            .ToDictionaryAsync(q => q.Id, q => q.Count, cancellationToken).ConfigureAwait(false);
        if (counts.Count != ids.Length)
            throw new DomainException(ErrorCode.NotFound, "Quests are unavailable or access has changed.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(result, counts);
    }
}
