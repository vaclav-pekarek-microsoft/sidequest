using Microsoft.AspNetCore.Components;
using NodaTime;
using Sidequest.Domain.Rules;

namespace Sidequest.Web.Components.Quests;

/// <summary>Labels UTC instants in the inherited IANA Event zone without guessing a user's zone.</summary>
public partial class QuestTimes
{
    /// <summary>Inherited Event zone identifier used for civil-time rendering.</summary>
    [Parameter, EditorRequired] public string ZoneId { get; set; } = "Etc/UTC";
    /// <summary>UTC activity start instant.</summary>
    [Parameter] public DateTimeOffset StartUtc { get; set; }
    /// <summary>UTC exclusive activity end instant.</summary>
    [Parameter] public DateTimeOffset EndUtc { get; set; }

    private string Local(DateTimeOffset value) =>
        Instant.FromDateTimeOffset(value).InZone(TimeRules.Zone(ZoneId)).ToString("yyyy-MM-dd HH:mm o<G>", null);
}
