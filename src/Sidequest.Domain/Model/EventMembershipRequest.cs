namespace Sidequest.Domain.Model;

/// <summary>Retained request for individual Event access; a pending request grants no membership.</summary>
public sealed class EventMembershipRequest : Entity
{
    /// <summary>Internal identifier of the requested Event.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the requester.</summary>
    public Guid UserId { get; set; }
    /// <summary>Current request decision or withdrawal state.</summary>
    public MembershipRequestStatus Status { get; set; }
    /// <summary>Decision explanation visible to the requester, including mandatory rejection reasons.</summary>
    public string Reason { get; set; } = "";
    /// <summary>UTC instant when the request was submitted.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC resolution instant, or null while no decision is recorded.</summary>
    public DateTimeOffset? DecidedUtc { get; set; }
    /// <summary>Internal decision-maker identifier, or null when no user decision-maker is recorded.</summary>
    public Guid? DecidedById { get; set; }
}
