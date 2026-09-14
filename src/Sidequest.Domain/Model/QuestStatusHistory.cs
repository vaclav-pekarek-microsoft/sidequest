namespace Sidequest.Domain.Model;

/// <summary>Retained Quest lifecycle transition; completion does not erase suspension or cancellation history.</summary>
public sealed class QuestStatusHistory : Entity
{
    /// <summary>Internal identifier of the Quest whose status changed.</summary>
    public Guid QuestId { get; set; }
    /// <summary>Lifecycle state before the transition.</summary>
    public QuestStatus Previous { get; set; }
    /// <summary>Lifecycle state after the transition.</summary>
    public QuestStatus Next { get; set; }
    /// <summary>Internal actor identifier, or null for a system transition.</summary>
    public Guid? ActorId { get; set; }
    /// <summary>Recorded explanation for the lifecycle transition.</summary>
    public string Reason { get; set; } = "";
    /// <summary>UTC instant of the transition.</summary>
    public DateTimeOffset OccurredUtc { get; set; }
}
