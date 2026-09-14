namespace Sidequest.Application.Events.Implementation;

/// <summary>Versioned reference to a durable one-time group expansion and frozen recipient snapshot.</summary>
/// <param name="SchemaVersion">Payload schema, currently one; unknown schemas are rejected.</param>
/// <param name="OperationId">Persisted operation containing the actor, Event, source group, mode, and progress.</param>
/// <param name="StartedVersion">Base64 SQL rowversion captured when the operation was created, before releasing its Event lock.
/// Legacy payloads use the earliest fence and conservatively skip historical revocations rather than risking stale restoration.</param>
public sealed record BulkMembershipPayload(int SchemaVersion, Guid OperationId, string StartedVersion = "AAAAAAAAAAA=");
