using Microsoft.AspNetCore.Components;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Binds list query values in the HTTP request and transfers them explicitly across the interactive render boundary.</summary>
public partial class QuestListRoute
{
    /// <summary>Gets or sets the requested list view; the interactive view validates it without inferring permission.</summary>
    [SupplyParameterFromQuery(Name = "view")]
    public string? View { get; set; }

    /// <summary>Gets or sets the optional Event filter, independently authorized by the application service.</summary>
    [SupplyParameterFromQuery(Name = "eventId")]
    public Guid? EventId { get; set; }
}
