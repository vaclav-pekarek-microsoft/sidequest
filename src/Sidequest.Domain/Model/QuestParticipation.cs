namespace Sidequest.Domain.Model;

/// <summary>Single mutually exclusive participation state for a Quest/user pair, independent of ownership and invitations.</summary>
public sealed class QuestParticipation : Entity
{
    /// <summary>Internal identifier of the Quest being joined or followed.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of the participant.</summary>
    public Guid UserId { get; set; }
    /// <summary>None, Following, or Joined; following and attendance cannot coexist.</summary>
    public ParticipationStatus Status { get; set; }
    /// <summary>UTC instant of the latest participation transition.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
}
