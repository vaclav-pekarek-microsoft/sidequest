namespace Sidequest.Domain.Model;

/// <summary>Durable per-recipient in-app item whose protected content must be reauthorized when read.</summary>
public sealed class Notification : Entity
{
    /// <summary>Internal account identifier of the sole recipient.</summary>
    public Guid UserId { get; set; }
    /// <summary>Stable originating change identifier used to coalesce repeated processing.</summary>
    public Guid SourceChangeId { get; set; }
    /// <summary>Business trigger determining recipient and delivery policy.</summary>
    public NotificationKind Kind { get; set; }
    /// <summary>Related Event identifier, or null for an item without an Event reference.</summary>
    public Guid? EventId { get; set; }
    /// <summary>Related Quest identifier, or null for an item without a Quest reference.</summary>
    public Guid? QuestId { get; set; }
    /// <summary>Stored display text; existing text must not expose protected content after access loss.</summary>
    public string Summary { get; set; } = "";
    /// <summary>Whether the item is a minimal access-loss notice eligible for disclosure without current content access.</summary>
    public bool IsAccessLossNotice { get; set; }
    /// <summary>UTC instant when the notification was created.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant when the recipient marked the item read, or null while unread.</summary>
    public DateTimeOffset? ReadUtc { get; set; }
}
