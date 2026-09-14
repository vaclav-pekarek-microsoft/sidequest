namespace Sidequest.Domain.Model;

/// <summary>One equal Event owner assignment; owners require individual membership and have no primary-owner rank.</summary>
public sealed class EventOwner : Entity
{
    /// <summary>Internal identifier of the managed Event.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the owner.</summary>
    public Guid UserId { get; set; }
}
