using Sidequest.Domain.Model;

namespace Sidequest.Application.Abstractions;

/// <summary>Version-1 durable business change payload; outbox type/schema, aggregate, and correlation metadata live on the outbox record.</summary>
/// <param name="ChangeId">Stable application-generated change identifier reused for idempotent processing.</param>
/// <param name="Kind">Business trigger selecting recipient and delivery policy.</param>
/// <param name="EventId">Internal parent Event identifier.</param>
/// <param name="QuestId">Internal affected Quest identifier, or null for an Event-level change.</param>
/// <param name="ActorId">Internal acting account identifier, or null for a system change.</param>
/// <param name="RecipientIds">Captured internal recipient identifiers; publication fan-out may resolve and persist its registered-member set during processing.</param>
/// <param name="OccurredUtc">UTC instant of the source change, retained across retries.</param>
/// <param name="CalendarRevision">Transactionally allocated Quest calendar revision, or zero when no revision is supplied.</param>
/// <param name="Reason">Safe action explanation; empty when no reason is supplied.</param>
/// <param name="PreviousAttendeeIds">Prior attendee snapshot for withdrawal/transition handling, or null when not supplied.</param>
/// <param name="CalendarChanged">Optional flag indicating a calendar-relevant content change; defaults to false for payload compatibility.</param>
/// <param name="MaterialChange">Optional flag indicating a material attendee change requiring mandatory delivery; defaults to false.</param>
/// <remarks>Recipients remain subject to current access and optional preferences at delivery; minimal access-loss notices and withdrawals use restricted historical data.
/// Record immutability is shallow: recipient arrays are not cloned or protected against mutation. Treat them as a stable snapshot
/// and do not modify them during staging, serialization, or concurrent reading.</remarks>
public sealed record ChangeEnvelope(
    Guid ChangeId,
    NotificationKind Kind,
    Guid EventId,
    Guid? QuestId,
    Guid? ActorId,
    Guid[] RecipientIds,
    DateTimeOffset OccurredUtc,
    long CalendarRevision = 0,
    string Reason = "",
    Guid[]? PreviousAttendeeIds = null,
    bool CalendarChanged = false,
    bool MaterialChange = false);
