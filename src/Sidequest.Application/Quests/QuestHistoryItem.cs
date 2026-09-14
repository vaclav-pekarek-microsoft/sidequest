namespace Sidequest.Application.Quests;

/// <summary>Authorized Quest action history, excluding protected moderation details from ordinary viewer projections.</summary>
/// <param name="Action">Recorded action label.</param>
/// <param name="Reason">Safe explanation visible in the selected history view.</param>
/// <param name="OccurredUtc">UTC action instant.</param>
/// <param name="Actor">Actor display label, or null when no user label is supplied, including system actions.</param>
public sealed record QuestHistoryItem(string Action, string Reason, DateTimeOffset OccurredUtc, string? Actor);
