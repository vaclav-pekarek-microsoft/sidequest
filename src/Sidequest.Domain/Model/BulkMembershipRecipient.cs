namespace Sidequest.Domain.Model;

/// <summary>Frozen individual bulk recipient and resumable outcome; later source-group changes do not alter this snapshot.</summary>
public sealed class BulkMembershipRecipient : Entity
{
    /// <summary>Internal identifier of the one-time bulk operation.</summary>
    public Guid OperationId { get; set; }
    /// <summary>Internal account identifier resolved during complete directory expansion.</summary>
    public Guid UserId { get; set; }
    /// <summary>Per-recipient processing outcome used to resume unfinished work without duplicate grants.</summary>
    public BulkRecipientStatus Status { get; set; }
    /// <summary>Safe outcome explanation, including skip/failure details, or null when no detail is recorded.</summary>
    public string? Detail { get; set; }
}
