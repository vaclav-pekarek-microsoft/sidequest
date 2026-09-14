namespace Sidequest.Application.Quests.Implementation;

/// <summary>Version-one completion intent; an obsolete end instant must never complete a rescheduled Quest.</summary>
/// <param name="QuestId">Internal Quest identifier, not an access grant.</param>
/// <param name="EndUtc">Exact scheduled UTC end instant captured by the producer.</param>
public sealed record QuestCompletionPayload(Guid QuestId, DateTimeOffset EndUtc);
