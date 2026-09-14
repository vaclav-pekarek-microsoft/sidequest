namespace Sidequest.Domain.Model;

/// <summary>Dated container for individually authorized members and Quests, with equal owners and an inherited child time zone.</summary>
public sealed class Event : Entity
{
    /// <summary>Internal creator account identifier retained for history, not an ongoing authorization grant.</summary>
    public Guid CreatorId { get; set; }
    /// <summary>Event name included in authorized discovery summaries.</summary>
    public string Name { get; set; } = "";
    /// <summary>Full content restricted to authorized Event readers, unlike the discovery summary.</summary>
    public string Description { get; set; } = "";
    /// <summary>Short description safe to disclose during eligible-user discovery of an Active Event.</summary>
    public string DiscoverySummary { get; set; } = "";
    /// <summary>Inclusive first local calendar date in <see cref="TimeZoneId"/>.</summary>
    public DateOnly StartDate { get; set; }
    /// <summary>Inclusive last local calendar date; completion occurs at the following local midnight.</summary>
    public DateOnly EndDate { get; set; }
    /// <summary>IANA zone inherited by every child Quest; fixed after publication.</summary>
    public string TimeZoneId { get; set; } = "Etc/UTC";
    /// <summary>Persisted lifecycle state; callers must also enforce time-based completion boundaries.</summary>
    public EventStatus Status { get; set; }
    /// <summary>UTC instant of Event creation.</summary>
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>UTC instant of the latest recorded Event update.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}
