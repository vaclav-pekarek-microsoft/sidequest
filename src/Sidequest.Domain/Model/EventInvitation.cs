namespace Sidequest.Domain.Model;

/// <summary>Named Event invitation requiring acceptance before membership; unlike a Quest invitation, it expires.</summary>
public sealed class EventInvitation : Entity
{
    /// <summary>Internal identifier of the Event offering membership.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier of the invited person.</summary>
    public Guid UserId { get; set; }
    /// <summary>Internal account identifier of the inviting manager.</summary>
    public Guid InvitedById { get; set; }
    /// <summary>Pending or terminal response/revocation/expiration state.</summary>
    public EventInvitationStatus Status { get; set; }
    /// <summary>UTC instant when the invitation was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC acceptance deadline, bounded by seven days and the Event's local end boundary.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }
    /// <summary>UTC resolution instant, or null while the invitation remains unresolved.</summary>
    public DateTimeOffset? ResolvedUtc { get; set; }
}
