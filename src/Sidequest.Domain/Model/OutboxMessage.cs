namespace Sidequest.Domain.Model;

/// <summary>Durable change work committed with domain state, then dispatched outside the SQL transaction using leases and retries.</summary>
public sealed class OutboxMessage : Entity
{
    /// <summary>Versioned work discriminator selecting the payload handler.</summary>
    public string Type { get; set; } = "";
    /// <summary>Payload schema metadata for compatible deserialization; defaults to version 1.</summary>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Internal affected aggregate identifier, typically Quest ID or its Event ID.</summary>
    public Guid AggregateId { get; set; }
    /// <summary>Serialized durable payload; retries must preserve the logical change and recipient basis.</summary>
    public string PayloadJson { get; set; } = "";
    /// <summary>Correlation identifier linking the originating change and subsequent work.</summary>
    public string CorrelationId { get; set; } = "";
    /// <summary>UTC instant when the originating change occurred.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
    /// <summary>Dispatch lifecycle state, including terminal failure and supersession.</summary>
    public WorkStatus Status { get; set; }
    /// <summary>Recorded processing attempt count used by the retry policy.</summary>
    public int Attempts { get; set; }
    /// <summary>UTC instant at or after which processing or retry becomes due.</summary>
    public DateTimeOffset DueUtc { get; set; }
    /// <summary>Current worker claim token, or null when no lease is recorded.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC lease expiry permitting abandoned work to be reclaimed, or null without a recorded lease.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    /// <summary>Last recorded safe failure detail, or null when no error is recorded.</summary>
    public string? LastError { get; set; }
}
