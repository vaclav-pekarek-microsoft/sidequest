using Sidequest.Application.Quests;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Quests;

/// <summary>Converts civil filter dates into instants without assuming every local day is twenty-four hours.</summary>
public static class QuestDateFilterFactory
{
    /// <summary>Builds a start-time filter covering inclusive local dates in the explicitly selected zone.</summary>
    /// <param name="from">Inclusive first local date, or null for no lower boundary.</param>
    /// <param name="through">Inclusive last local date, or null for no upper boundary.</param>
    /// <param name="zoneId">Selected Event's IANA zone, or explicitly labeled Etc/UTC for all Events.</param>
    /// <returns>Inclusive lower and exclusive upper UTC start-instant boundaries.</returns>
    /// <exception cref="DomainException">The range is reversed, a date is unsupported/skipped, or the zone is invalid.</exception>
    public static QuestDateFilter FromDates(DateOnly? from, DateOnly? through, string zoneId)
    {
        if (from is not null && through is not null && through < from)
            throw new DomainException(ErrorCode.Validation, "The last date must not precede the first date.", "Dates");
        TimeRules.Zone(zoneId);
        return new(
            from is null ? null : TimeRules.EventWindow(from.Value, from.Value, zoneId).Start.ToUniversalTime(),
            through is null ? null : TimeRules.EventWindow(through.Value, through.Value, zoneId).End.ToUniversalTime());
    }
}
