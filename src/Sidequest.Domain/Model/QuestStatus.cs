namespace Sidequest.Domain.Model;

/// <summary>Quest lifecycle states, separate from participation and access grants.</summary>
public enum QuestStatus
{
    /// <summary>Unpublished activity visible only to its owners.</summary>
    Draft,
    /// <summary>Published activity permitting new participation while its Event is Active and the Quest has not ended.</summary>
    Active,
    /// <summary>Moderator suspension freezing new participation and withdrawing calendars, while allowing owner edits.</summary>
    Suspended,
    /// <summary>Activity ended without automatically cancelling historical calendar entries.</summary>
    Completed,
    /// <summary>Activity cancelled; valid invitees retain historical read access while Event membership remains.</summary>
    Cancelled,
    /// <summary>Read-only history hidden from default lists, without freezing access grants permanently.</summary>
    Archived
}
