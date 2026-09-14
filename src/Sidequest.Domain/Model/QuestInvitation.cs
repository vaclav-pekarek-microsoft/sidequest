namespace Sidequest.Domain.Model;

/// <summary>Identity-bound private Quest access grant, without acceptance, expiration, or automatic participation.</summary>
public sealed class QuestInvitation : Entity
{
    /// <summary>Internal identifier of the private Quest.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal invited account identifier; access also requires current Event membership.</summary>
    public Guid UserId { get; set; }
    /// <summary>Internal account identifier of the inviting Quest owner.</summary>
    public Guid InvitedById { get; set; }
    /// <summary>Whether the invitation-derived access grant remains active or has been revoked.</summary>
    public QuestInvitationStatus Status { get; set; }
    /// <summary>UTC instant of the latest grant or revocation.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
}
