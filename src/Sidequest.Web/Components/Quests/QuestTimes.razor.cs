using Microsoft.AspNetCore.Components;

namespace Sidequest.Web.Components.Quests;

/// <summary>Composes Event-primary times and the detected device-zone equivalent without guessing or persisting a user's zone.</summary>
public partial class QuestTimes
{
    /// <summary>Inherited Event zone identifier used for civil-time rendering.</summary>
    [Parameter, EditorRequired] public string ZoneId { get; set; } = "Etc/UTC";
    /// <summary>UTC activity start instant.</summary>
    [Parameter] public DateTimeOffset StartUtc { get; set; }
    /// <summary>UTC exclusive activity end instant.</summary>
    [Parameter] public DateTimeOffset EndUtc { get; set; }
}
