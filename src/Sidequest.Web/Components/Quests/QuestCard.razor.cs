using Microsoft.AspNetCore.Components;
using Sidequest.Application.Quests;

namespace Sidequest.Web.Components.Quests;

/// <summary>Displays an already-authorized Quest summary without reconstructing suppressed counts.</summary>
public partial class QuestCard
{
    /// <summary>Privacy-filtered card data provided by the parent query.</summary>
    [Parameter, EditorRequired] public QuestSummary Item { get; set; } = default!;
    /// <summary>Uses the dedicated moderation route rather than implying ordinary private access.</summary>
    [Parameter] public bool Moderation { get; set; }
}
