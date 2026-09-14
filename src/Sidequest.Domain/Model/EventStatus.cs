namespace Sidequest.Domain.Model;

/// <summary>Event lifecycle states; terminal states retain authorized historical access.</summary>
public enum EventStatus
{
    /// <summary>Unpublished configuration visible only to Event owners.</summary>
    Draft,
    /// <summary>Published Event, subject to its local date completion boundary.</summary>
    Active,
    /// <summary>Event ended; new activity stops without calendar cancellation.</summary>
    Completed,
    /// <summary>Event explicitly cancelled, with applicable child cancellation and delivery cleanup.</summary>
    Cancelled,
    /// <summary>Read-only historical Event; access revocation and required bookkeeping remain possible.</summary>
    Archived
}
