using Microsoft.AspNetCore.Components;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Transfers request-bound Quest identity and moderation intent to the separately authorized interactive view.</summary>
public partial class QuestDetailsRoute
{
    /// <summary>Gets or sets the requested Quest identifier; knowing it does not grant access.</summary>
    [Parameter]
    public Guid Id { get; set; }

    /// <summary>Gets or sets explicit moderation intent, not ordinary access or an Event-owner permission grant.</summary>
    [SupplyParameterFromQuery(Name = "moderation")]
    public bool Moderation { get; set; }
}
