namespace Sidequest.Application.Events;

/// <summary>Potentially duplicate discoverable Event presented as a warning rather than an automatic creation ban.</summary>
/// <param name="Event">Only the Event content the actor is permitted to discover.</param>
/// <param name="Similarity">Normalized name-word Jaccard similarity from zero through one; duplicate warnings use at least 0.6 or identical normalized names.</param>
public sealed record DuplicateEvent(EventSummary Event, double Similarity);
