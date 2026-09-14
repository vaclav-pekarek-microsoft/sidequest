namespace Sidequest.Domain.Model;

/// <summary>Durable timed work for lifecycle cleanup, reminders, or bulk processing, with repeat-safe leasing.</summary>
public sealed class ScheduledWork : Entity
{
    /// <summary>Versioned work discriminator selecting the execution handler.</summary>
    public string Type { get; set; } = "";
    /// <summary>Stable logical-work key preventing duplicate scheduling or replay effects.</summary>
    public string DeduplicationKey { get; set; } = "";
    /// <summary>Associated Quest identifier, or null for work not scoped to a Quest.</summary>
    public Guid? QuestId { get; set; }
    /// <summary>Associated internal user identifier, or null for work not scoped to a recipient.</summary>
    public Guid? UserId { get; set; }
    /// <summary>Serialized work input interpreted according to the versioned work type.</summary>
    public string PayloadJson { get; set; } = "";
    /// <summary>Execution lifecycle state used for claims, retries, and terminal outcomes.</summary>
    public WorkStatus Status { get; set; }
    /// <summary>UTC instant at or after which execution or retry is due.</summary>
    public DateTimeOffset DueUtc { get; set; }
    /// <summary>Recorded execution attempt count used by the retry policy.</summary>
    public int Attempts { get; set; }
    /// <summary>Current worker claim token, or null when no lease is recorded.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC claim expiry, or null without a recorded lease.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    /// <summary>Last recorded safe failure detail, or null when no error is recorded.</summary>
    public string? LastError { get; set; }
}
