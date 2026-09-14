namespace Sidequest.Domain.Model;

/// <summary>Ordinary Quest visibility within the parent Event's individual membership boundary.</summary>
public enum QuestVisibility
{
    /// <summary>Non-draft content readable by eligible Event members.</summary>
    Public,
    /// <summary>Ordinary access limited to named invitees and Quest owners; separate moderation rules apply.</summary>
    Private
}
