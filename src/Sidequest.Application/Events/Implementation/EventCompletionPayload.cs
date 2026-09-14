namespace Sidequest.Application.Events.Implementation;

/// <summary>Versioned completion intent; handlers compare the saved boundary to the Event's current dates.</summary>
/// <param name="SchemaVersion">Payload schema, currently one; unknown schemas are rejected.</param>
/// <param name="EventId">Internal Event to reconcile, never an authorization grant.</param>
/// <param name="ExpectedEndUtc">Exclusive UTC end boundary at scheduling time; obsolete work makes no changes.</param>
public sealed record EventCompletionPayload(int SchemaVersion, Guid EventId, DateTimeOffset ExpectedEndUtc);
