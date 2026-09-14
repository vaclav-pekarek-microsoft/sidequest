namespace Sidequest.Domain.Model;

/// <summary>Retained Event lifecycle transition, including prior state and user or system attribution.</summary>
public sealed class EventStatusHistory : Entity
{
    /// <summary>Internal identifier of the Event whose status changed.</summary>
    public Guid EventId { get; set; }
    /// <summary>Lifecycle state before the transition.</summary>
    public EventStatus Previous { get; set; }
    /// <summary>Lifecycle state after the transition.</summary>
    public EventStatus Next { get; set; }
    /// <summary>Internal actor identifier, or null for a system transition.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Recorded explanation for the lifecycle transition.</summary>
    public string Reason { get; set; } = "";
    /// <summary>UTC instant of the transition.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
}
