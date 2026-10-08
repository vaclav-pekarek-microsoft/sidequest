namespace Sidequest.Domain.Model;

/// <summary>Ordinary Quest visibility within the parent Event's individual membership boundary.</summary>
public enum QuestVisibility
{
    /// <summary>Non-draft content readable by eligible Event members.</summary>
    Public,
    /// <summary>Access limited to named invitees, Quest owners, and administrators.</summary>
    Private
}
