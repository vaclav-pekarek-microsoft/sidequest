namespace Sidequest.Domain.Model;

/// <summary>Per-Quest/recipient calendar ordering and uncertainty state used to supersede stale deliveries safely.</summary>
public sealed class CalendarDeliveryState : Entity
{
    /// <summary>Internal Quest identifier associated with the stable calendar UID.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Internal account identifier of the sole calendar recipient.</summary>
    public Guid UserId { get; set; }
    /// <summary>Latest desired iCalendar sequence allocated from the Quest's monotonic calendar revision.</summary>
    public long IntendedSequence { get; set; }
    /// <summary>Last recorded sent sequence, or null before a successful send is recorded.</summary>
    public long? SentSequence { get; set; }
    /// <summary>Desired iTIP method, such as REQUEST or CANCEL.</summary>
    public string IntendedMethod { get; set; } = "";
    /// <summary>Serialized calendar content retained unchanged for retries of the same logical delivery.</summary>
    public string Payload { get; set; } = "";
    /// <summary>Whether prior delivery may have reached the provider, requiring withdrawal even after an uncertain outcome.</summary>
    public bool MayHaveBeenDelivered { get; set; }
    /// <summary>UTC instant of the latest calendar delivery-state change.</summary>
    public DateTimeOffset ChangedUtc { get; set; }
}
