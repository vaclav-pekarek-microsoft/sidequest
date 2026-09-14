namespace Sidequest.Domain.Model;

/// <summary>Retained audit record of a resource action, written transactionally with its state change.</summary>
public sealed class AuditEntry : Entity
{
    /// <summary>Category identifying the namespace of the audited resource.</summary>
    public ResourceKind ResourceKind { get; set; }
    /// <summary>Internal identifier of the audited resource.</summary>
    public Guid ResourceId { get; set; }
    /// <summary>Internal acting account identifier, or null for a system action.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Stable action label describing the audited operation.</summary>
    public string Action { get; set; } = "";
    /// <summary>Safe explanation of the action, without secrets or unauthorized content.</summary>
    public string Reason { get; set; } = "";
    /// <summary>Identifier linking related state, audit, and durable-delivery work.</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>UTC instant when the action occurred.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
}
