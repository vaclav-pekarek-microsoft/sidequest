namespace Sidequest.Application.Events;

/// <summary>Event detail with full content omitted when the actor has discovery-only access.</summary>
/// <param name="Summary">Authorized Event summary.</param>
/// <param name="Description">Full description for an authorized reader, or null when privacy limits the result to discovery content.</param>
public sealed record EventDetail(EventSummary Summary, string? Description);
