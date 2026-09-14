namespace Sidequest.Domain.Model;

/// <summary>Durable per-recipient delivery attempt state; provider acceptance is not guaranteed mailbox arrival.</summary>
public sealed class NotificationDelivery : Entity
{
    /// <summary>Internal identifier of the corresponding in-app notification.</summary>
    public Guid NotificationId { get; set; }
    /// <summary>Internal account identifier of the sole delivery recipient.</summary>
    public Guid UserId { get; set; }
    /// <summary>Stable logical delivery key reused across retries and administrative replay.</summary>
    public string DeduplicationKey { get; set; } = "";
    /// <summary>Serialized delivery input; dispatch must recheck current access and optional preferences.</summary>
    public string PayloadJson { get; set; } = "";
    /// <summary>Delivery processing state, including visible dead-letter failures.</summary>
    public WorkStatus Status { get; set; }
    /// <summary>UTC instant at or after which the next attempt is due.</summary>
    public DateTimeOffset DueUtc { get; set; }
    /// <summary>Recorded delivery attempt count.</summary>
    public int Attempts { get; set; }
    /// <summary>Current dispatcher claim token, or null without a recorded lease.</summary>
    public Guid? LeaseId { get; set; }
    /// <summary>UTC claim expiry permitting recovery, or null without a recorded lease.</summary>
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    /// <summary>Provider acceptance identifier, or null when no acceptance identifier was recorded.</summary>
    public string? ProviderMessageId { get; set; }
    /// <summary>Last recorded safe delivery failure detail, or null when none is recorded.</summary>
    public string? LastError { get; set; }
}
