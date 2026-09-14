namespace Sidequest.Domain.Model;

/// <summary>Authoritative individual Event/user access record; group expansion never creates an ongoing group grant.</summary>
public sealed class EventMembership : Entity
{
    /// <summary>Internal identifier of the Event whose access is controlled.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the individual member.</summary>
    public Guid UserId { get; set; }
    /// <summary>Whether individual membership is active or has been deactivated; eligibility is checked separately.</summary>
    public MembershipStatus Status { get; set; }
    /// <summary>UTC instant of the latest membership state change.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
    /// <summary>Internal account identifier responsible for the latest membership state change.</summary>
    public Guid ChangedById { get; set; }
}
