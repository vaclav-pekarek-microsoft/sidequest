using NodaTime;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Quests;

/// <summary>Maps Quest wall-clock choices through the same bundled time-zone rules used by server scheduling validation.</summary>
public static class QuestLocalTime
{
    /// <summary>Returns chronologically ordered offsets for a local time without choosing a daylight-saving occurrence.</summary>
    /// <param name="local">Wall-clock components in the inherited Event zone; the DateTime kind is ignored.</param>
    /// <param name="zoneId">The Event's IANA time-zone identifier.</param>
    /// <returns>No offsets for a nonexistent time, one for an ordinary time, or first and second occurrence offsets for a repeated time.</returns>
    /// <exception cref="DomainException">The Event zone is unknown.</exception>
    public static IReadOnlyList<TimeSpan> Candidates(DateTime local, string zoneId)
    {
        var mapping = TimeRules.Zone(zoneId).MapLocal(
            LocalDateTime.FromDateTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));
        return mapping.Count switch
        {
            0 => [],
            1 => [mapping.Single().Offset.ToTimeSpan()],
            _ => [mapping.First().Offset.ToTimeSpan(), mapping.Last().Offset.ToTimeSpan()]
        };
    }
}
