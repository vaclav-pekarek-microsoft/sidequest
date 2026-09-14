using Sidequest.Domain.Model;

namespace Sidequest.Application.Events;

/// <summary>Event projection usable for authorized discovery without exposing full descriptions, membership rosters, or counts.</summary>
/// <param name="Id">Internal Event identifier.</param>
/// <param name="Name">Event display name.</param>
/// <param name="DiscoverySummary">Short discovery-safe description.</param>
/// <param name="StartDate">Inclusive first date in the Event's zone.</param>
/// <param name="EndDate">Inclusive last date in the Event's zone.</param>
/// <param name="TimeZoneId">IANA Event zone inherited by its Quests.</param>
/// <param name="Status">Lifecycle state presented by the authorized query.</param>
/// <param name="Owners">Equal owners' directory contacts; no primary-owner rank.</param>
/// <param name="IsMember">Whether the current actor has active individual membership.</param>
/// <param name="IsOwner">Whether the current actor is an Event owner.</param>
/// <param name="Version">Opaque Base64 SQL rowversion for optimistic concurrency, not a time value.</param>
/// <remarks>The owner list is not defensively copied; its producer must keep the published snapshot stable during concurrent reads.</remarks>
public sealed record EventSummary(Guid Id, string Name, string DiscoverySummary, DateOnly StartDate,
    DateOnly EndDate, string TimeZoneId, EventStatus Status, IReadOnlyList<OwnerSummary> Owners,
    bool IsMember, bool IsOwner, string Version);
