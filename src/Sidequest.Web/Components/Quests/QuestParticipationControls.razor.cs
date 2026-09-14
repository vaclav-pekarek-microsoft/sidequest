using Microsoft.AspNetCore.Components;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;

namespace Sidequest.Web.Components.Quests;

/// <summary>Presents exclusive participation choices without treating visibility of controls as authorization.</summary>
public partial class QuestParticipationControls
{
    /// <summary>Current authorized lifecycle and participation snapshot.</summary>
    [Parameter, EditorRequired] public QuestSummary Summary { get; set; } = default!;
    /// <summary>Prevents another command while one is pending.</summary>
    [Parameter] public bool Busy { get; set; }
    /// <summary>Requests a server-authorized participation transition.</summary>
    [Parameter] public EventCallback<ParticipationCommand> Change { get; set; }
}
