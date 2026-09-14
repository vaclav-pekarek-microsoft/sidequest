namespace Sidequest.Domain.Model;

/// <summary>Event-specific override of a user's optional new-Quest email preference.</summary>
public sealed class EventNotificationPreference : Entity
{
    /// <summary>Internal identifier of the Event to which the override applies.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal account identifier choosing the override.</summary>
    public Guid UserId { get; set; }
    /// <summary>Whether optional new-Quest email is enabled for this Event instead of the user default.</summary>
    public bool NewQuestEmail { get; set; }
}
