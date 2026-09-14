namespace Sidequest.Domain.Model;

/// <summary>Repeat-safe user commands over the exclusive participation state.</summary>
public enum ParticipationCommand
{
    /// <summary>Start following; conflicts rather than silently leaving when already Joined.</summary>
    Follow,
    /// <summary>Stop following without changing attendance.</summary>
    Unfollow,
    /// <summary>Attend, atomically replacing Following if present.</summary>
    Join,
    /// <summary>Stop attending without starting or restoring Following.</summary>
    Leave
}
