using Microsoft.AspNetCore.Components;
using Sidequest.Application.Events;
using Sidequest.Application.Quests;

namespace Sidequest.Web.Components.Quests;

/// <summary>Collects explicit, confirmed owner/moderator intentions; never invokes persistence directly.</summary>
public partial class QuestManagement
{
    private string selected = "";
    private string reason = "";
    private string action = "";
    private bool confirmed;
    private QuestManagementDraft? previousDraft;
    private bool Disabled => Busy || !confirmed;
    private bool TargetDisabled => Disabled || !Guid.TryParse(selected, out _);
    private string CurrentAction => Moderation ? Detail.Summary.Status switch
    {
        Sidequest.Domain.Model.QuestStatus.Active => "suspend",
        Sidequest.Domain.Model.QuestStatus.Suspended => "reinstate",
        _ => ""
    } : action;
    private bool NeedsPerson => CurrentAction is "add-owner" or "remove-owner" or "invite" or "revoke" or "remove-attendee";
    private bool NeedsReason => CurrentAction is "cancel" or "suspend" or "reinstate" or "revoke" or "remove-attendee";
    private string ActionLabel => CurrentAction switch
    {
        "publish" => "Publish draft", "delete" => "Delete draft", "cancel" => "Cancel Quest",
        "archive" => "Archive", "add-owner" => "Add equal owner", "remove-owner" => "Remove owner access",
        "invite" => "Invite (immediate access)", "revoke" => "Revoke invitation",
        "remove-attendee" => "Remove attendee", "suspend" => "Suspend",
        "reinstate" => "Reinstate with latest details", _ => ""
    };
    private string ConfirmationDescription => CurrentAction switch
    {
        "cancel" or "suspend" => "Attendee calendars will be withdrawn.",
        "reinstate" => "The latest details will be used to restore eligible attendee calendars.",
        "revoke" or "remove-attendee" => "This can end access or attendance and withdraw the person's calendar entry.",
        "add-owner" or "remove-owner" => "This changes equal management rights, not attendance.",
        "invite" => "This grants immediate access without joining the person.",
        "publish" => "The Quest will become available to its audience.",
        "delete" => "The draft will be permanently deleted.",
        _ => "The Quest will become read-only."
    };
    private IEnumerable<PersonSummary> Candidates => Members.Concat(Detail.Owners.Select(o => new PersonSummary(o.Id, o.DisplayName, o.Email)))
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
        action = Draft.Action;
        confirmed = false;
    }

    private Task PublishDraftAsync() => DraftChanged.InvokeAsync(new(selected, reason, action));

    private async Task SelectActionAsync(string next)
    {
        if (Busy)
            return;
        if (action != next)
        {
            selected = "";
            reason = "";
        }
        action = next;
        confirmed = false;
        await PublishDraftAsync();
    }

    private async Task SelectPersonAsync(string person)
    {
        selected = person;
        confirmed = false;
        await PublishDraftAsync();
    }

    private async Task SendAsync(string action)
    {
        if (Disabled || action != CurrentAction || (NeedsPerson && TargetDisabled))
            return;
        confirmed = false;
        await Execute.InvokeAsync(new(action, Guid.TryParse(selected, out var id) ? id : null, reason));
    }
}
