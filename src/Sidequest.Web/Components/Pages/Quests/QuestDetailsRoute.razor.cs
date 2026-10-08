using Microsoft.AspNetCore.Components;

namespace Sidequest.Web.Components.Pages.Quests;

/// <summary>Transfers the request-bound Quest identity to the separately authorized interactive view.</summary>
public partial class QuestDetailsRoute
{
    /// <summary>Gets or sets the requested Quest identifier; knowing it does not grant access.</summary>
    [Parameter]
    public Guid Id { get; set; }
}
