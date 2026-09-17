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
    private QuestManagementDraft? previousDraft;
    private bool Disabled => Busy || !confirmed;
    private bool TargetDisabled => Disabled || !Guid.TryParse(selected, out _);
    private IEnumerable<PersonSummary> Candidates => Members.Concat(Detail.Owners.Select(o => new PersonSummary(o.Id, o.DisplayName)))
        .DistinctBy(x => x.Id).OrderBy(x => x.DisplayName).ThenBy(x => x.Id);

    /// <summary>Authorized detail; moderation mode must have null protected rosters.</summary>
    [Parameter, EditorRequired] public QuestDetail Detail { get; set; } = default!;
    /// <summary>Selects only dedicated moderation actions, not ordinary ownership actions.</summary>
    [Parameter] public bool Moderation { get; set; }
    /// <summary>Blocks editing and confirmation while operations, revalidation, disconnection, or version conflicts prevent safe commands.</summary>
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
    /// <remarks>Callback completion is not an acknowledgement of success. The parent replaces <see cref="Draft"/> only after a committed action or an explicit discard.</remarks>
    [Parameter] public EventCallback<QuestActionRequest> Execute { get; set; }
    /// <summary>Page-owned unsent input restored after temporary removal of the authorized projection; confirmation is never restored.</summary>
    [Parameter] public QuestManagementDraft Draft { get; set; } = new();
    /// <summary>Publishes entered person and reason changes before a command can consume them; no mutation is queued or authorized by this callback.</summary>
    [Parameter] public EventCallback<QuestManagementDraft> DraftChanged { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (ReferenceEquals(previousDraft, Draft))
            return;
        previousDraft = Draft;
        selected = Draft.SelectedPerson;
        reason = Draft.Reason;
    }

    private Task PublishDraftAsync() => DraftChanged.InvokeAsync(new(selected, reason));

    private async Task SendAsync(string action)
    {
        if (Disabled)
            return;
        confirmed = false;
        await Execute.InvokeAsync(new(action, Guid.TryParse(selected, out var id) ? id : null, reason));
    }
}
