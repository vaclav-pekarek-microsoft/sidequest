using System.Text;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Events.Implementation;

internal static class EventQueries
{
    internal static async Task<EventSummary> SummaryAsync(ISidequestDbContext db, Event item,
        Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var member = await db.EventMemberships.AnyAsync(x => x.EventId == item.Id && x.UserId == userId &&
            x.Status == MembershipStatus.Active, cancellationToken).ConfigureAwait(false);
        var owners = await (from owner in db.EventOwners
                            join user in db.Users on owner.UserId equals user.Id
                            where owner.EventId == item.Id && user.IsEligible && user.DepartureVerifiedUtc == null
                            orderby user.DisplayName, user.Id
                            select new OwnerSummary(user.Id, user.DisplayName, user.Email))
                            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ownerAccess = member && owners.Any(x => x.Id == userId);
        var status = EventTransactions.Effective(item, now);
        var unpublished = item.Status == EventStatus.Draft ||
            await db.EventStatusHistory.AnyAsync(x => x.EventId == item.Id &&
                x.Previous == EventStatus.Draft && x.Next == EventStatus.Cancelled, cancellationToken).ConfigureAwait(false);
        if (unpublished ? !ownerAccess : !member && status != EventStatus.Active)
            throw EventTransactions.Unavailable();
        return new EventSummary(item.Id, item.Name, item.DiscoverySummary, item.StartDate, item.EndDate,
            item.TimeZoneId, status, owners, member, ownerAccess, member ? Convert.ToBase64String(item.Version) : "");
    }

    internal static string Normalize(string name)
    {
        var result = new StringBuilder(name.Length);
        var space = true;
        foreach (var character in name.ToUpperInvariant())
        {
            if (!char.IsPunctuation(character) && !char.IsWhiteSpace(character))
            {
                result.Append(character);
                space = false;
            }
            else if (!space)
            {
                result.Append(' ');
                space = true;
            }
        }
        return result.ToString().TrimEnd();
    }

    internal static double Similarity(string first, string second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        if (left.Length == 0 || right.Length == 0)
            return 0;
        if (left == right)
            return 1;
        var a = left.Split(' ').ToHashSet(StringComparer.Ordinal);
        var b = right.Split(' ').ToHashSet(StringComparer.Ordinal);
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }
}
