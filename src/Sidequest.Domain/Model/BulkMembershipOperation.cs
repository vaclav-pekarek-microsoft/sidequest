namespace Sidequest.Domain.Model;

/// <summary>One-time group expansion workflow that snapshots users before applying individual add/invite operations.</summary>
public sealed class BulkMembershipOperation : Entity
{
    /// <summary>Internal identifier of the target Event.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal initiating manager identifier, reauthorized for each recipient operation.</summary>
    public Guid ActorId { get; set; }
    /// <summary>Entra source group object ID retained only for workflow/audit; never an authorization grant or synchronization link.</summary>
    public Guid SourceGroupId { get; set; }
    /// <summary>Whether snapshot recipients receive direct membership additions or consent-based invitations.</summary>
    public BulkMode Mode { get; set; }
    /// <summary>Expansion/application progress state, including visible failure.</summary>
    public BulkStatus Status { get; set; }
    /// <summary>UTC instant when the explicit bulk action was initiated.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant when the complete recipient snapshot was recorded, or null before completion; not an atomic directory snapshot time.</summary>
    public DateTimeOffset? SnapshotUtc { get; set; }
    /// <summary>Last recorded safe workflow failure detail, or null when none is recorded.</summary>
    public string? LastError { get; set; }
}
