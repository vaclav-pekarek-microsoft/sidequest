using Microsoft.AspNetCore.Components;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;

namespace Sidequest.Web.Components.Quests;

/// <summary>Collects explicit, confirmed owner/moderator intentions; never invokes persistence directly.</summary>
public partial class QuestManagement
{
    private string selected = "";
    private string reason = "";
    private bool confirmed;
    private bool Disabled => Busy || !confirmed;
    private bool TargetDisabled => Disabled || !Guid.TryParse(selected, out _);
    private IEnumerable<PersonSummary> Candidates => Members.Concat(Detail.Owners.Select(o => new PersonSummary(o.Id, o.DisplayName)))
        .DistinctBy(x => x.Id).OrderBy(x => x.DisplayName).ThenBy(x => x.Id);

    /// <summary>Authorized detail; moderation mode must have null protected rosters.</summary>
    [Parameter, EditorRequired] public QuestDetail Detail { get; set; } = default!;
    /// <summary>Selects only dedicated moderation actions, not ordinary ownership actions.</summary>
    [Parameter] public bool Moderation { get; set; }
    /// <summary>Disables duplicate actions while a command is pending.</summary>
    [Parameter] public bool Busy { get; set; }
    /// <summary>One authorized page of individual Event members; not fetched in moderation mode.</summary>
    [Parameter] public IReadOnlyList<PersonSummary> Members { get; set; } = [];
    /// <summary>Current one-based member-selection page.</summary>
    [Parameter] public int MemberPage { get; set; } = 1;
    /// <summary>Whether another page of member candidates is available.</summary>
    [Parameter] public bool MoreMembers { get; set; }
    /// <summary>Requests an authorized candidate page from the parent.</summary>
    [Parameter] public EventCallback<int> PageChanged { get; set; }
    /// <summary>Passes confirmed intent to the parent for authoritative service execution.</summary>
    [Parameter] public EventCallback<QuestActionRequest> Execute { get; set; }

    private async Task SendAsync(string action)
    {
        if (Disabled)
            return;
        confirmed = false;
        await Execute.InvokeAsync(new(action, Guid.TryParse(selected, out var id) ? id : null, reason));
        reason = "";
    }
}
