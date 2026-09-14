namespace Sidequest.Domain.Model;

/// <summary>Equal Quest owner assignment granting private access but never automatically joining or following.</summary>
public sealed class QuestOwner : Entity
{
    /// <summary>Internal identifier of the managed Quest.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of an owner who must also be an Event member.</summary>
    public Guid UserId { get; set; }
}
