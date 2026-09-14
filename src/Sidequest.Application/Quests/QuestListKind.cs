namespace Sidequest.Application.Quests;

/// <summary>Authorized Quest views; moderation is a separate privacy-restricted access path.</summary>
public enum QuestListKind
{
    /// <summary>Current actor's joined activities, excluding follower-only participation.</summary>
    Joined,
    /// <summary>Current actor's followed activities, excluding joined attendance.</summary>
    Following,
    /// <summary>Activities the actor owns, without implying attendance.</summary>
    Organizing,
    /// <summary>Discoverable public activities within the actor's individually joined Events.</summary>
    Discover,
    /// <summary>Private activities accessible through the actor's active named invitations.</summary>
    Invited,
    /// <summary>Past activities still accessible to the current actor.</summary>
    History,
    /// <summary>Non-draft activities moderated by an Event owner, without participant or invitation roster disclosure.</summary>
    Moderation
}
