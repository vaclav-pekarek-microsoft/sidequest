using Sidequest.Domain.Model;

namespace Sidequest.Domain.Rules;

public static class ParticipationRules
{
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
