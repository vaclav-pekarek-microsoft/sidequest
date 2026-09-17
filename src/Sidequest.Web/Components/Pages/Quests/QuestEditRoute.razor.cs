using Microsoft.AspNetCore.Components;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Transfers request-bound editor identity and Event preselection without depending on circuit navigation timing.</summary>
public partial class QuestEditRoute
{
    /// <summary>Gets or sets the existing Quest identifier, or null when creating a draft.</summary>
    [Parameter]
    public Guid? Id { get; set; }

    /// <summary>Gets or sets optional Event preselection; membership and lifecycle are checked by the interactive editor's service calls.</summary>
    [SupplyParameterFromQuery(Name = "eventId")]
    public Guid? EventId { get; set; }
}
