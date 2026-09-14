using Sidequest.Domain.Model;

namespace Sidequest.Domain.Rules;

/// <summary>Pure transitions for exclusive None/Following/Joined participation; callers enforce access, lifecycle, persistence, and delivery.</summary>
public static class ParticipationRules
{
    /// <summary>Computes a repeat-safe participation transition without mutating stored state.</summary>
    /// <param name="current">Existing exclusive participation state.</param>
    /// <param name="command">Requested transition; joining replaces following, while leaving never restores it.</param>
    /// <returns>The resulting state, including the unchanged state for an idempotent command.</returns>
    /// <exception cref="DomainException">An enum value is undefined (Validation), or Follow is requested while Joined (Conflict).</exception>
    public static ParticipationStatus Apply(ParticipationStatus current, ParticipationCommand command)
    {
        if (!Enum.IsDefined(current) || !Enum.IsDefined(command))
            throw new DomainException(ErrorCode.Validation, "Unknown participation state or command.");
        return (current, command) switch
        {
            (ParticipationStatus.Joined, ParticipationCommand.Follow) =>
                throw new DomainException(ErrorCode.Conflict, "You already joined this Quest and receive attendee updates."),
            (_, ParticipationCommand.Join) => ParticipationStatus.Joined,
            (_, ParticipationCommand.Follow) => ParticipationStatus.Following,
            (ParticipationStatus.Joined, ParticipationCommand.Leave) => ParticipationStatus.None,
            (ParticipationStatus.Following, ParticipationCommand.Unfollow) => ParticipationStatus.None,
            (_, ParticipationCommand.Leave or ParticipationCommand.Unfollow) => current,
            _ => throw new DomainException(ErrorCode.Validation, "Unknown participation command.")
        };
    }
}
