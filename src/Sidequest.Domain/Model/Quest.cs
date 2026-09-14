namespace Sidequest.Domain.Model;

/// <summary>Activity inside an Event's date window, inheriting its zone and using separate ownership, invitation, and participation records.</summary>
public sealed class Quest : Entity
{
    /// <summary>Internal parent Event identifier; the parent supplies the time zone and membership boundary.</summary>
    public Guid EventId { get; set; }
    /// <summary>Internal creator identifier retained for history, without special privileges after creation.</summary>
    public Guid CreatorId { get; set; }
    /// <summary>Activity title disclosed only through authorized views.</summary>
    public string Title { get; set; } = "";
    /// <summary>Activity details included in authorized content and calendar updates.</summary>
    public string Description { get; set; } = "";
    /// <summary>Free-form physical or online meeting location.</summary>
    public string Location { get; set; } = "";
    /// <summary>Advisory attendee capacity from 1 through 10000, or null for no suggestion; not an attendance limit.</summary>
    public int? SuggestedCapacity { get; set; }
    /// <summary>UTC start instant within the parent Event's local date window.</summary>
    public DateTimeOffset StartUtc { get; set; }
    /// <summary>UTC exclusive end instant, strictly after start and no later than the Event window end.</summary>
    public DateTimeOffset EndUtc { get; set; }
    /// <summary>Member discovery/access policy, fixed after publication.</summary>
    public QuestVisibility Visibility { get; set; }
    /// <summary>Persisted lifecycle state; editing a suspended Quest does not reinstate it.</summary>
    public QuestStatus Status { get; set; }
    /// <summary>Participant-facing explanation of the recorded lifecycle state.</summary>
    public string StatusReason { get; set; } = "";
    /// <summary>Internal cover media identifier, or null when no cover is assigned.</summary>
    public Guid? CoverAssetId { get; set; }
    /// <summary>Monotonic revision allocated transactionally for calendar-affecting changes, including recipient withdrawals.</summary>
    public long CalendarRevision { get; set; }
    /// <summary>Start-time revision used to deduplicate reminders per Quest and recipient.</summary>
    public long StartRevision { get; set; }
    /// <summary>UTC instant of Quest creation.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant of the latest recorded Quest update.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}
