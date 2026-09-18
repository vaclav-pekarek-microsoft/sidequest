namespace Sidequest.Application.Quests;

/// <summary>Authorized Quest action history, excluding protected moderation details from ordinary viewer projections.</summary>
/// <param name="Action">Authorized action label with persisted people labels in place of internal account identifiers.</param>
/// <param name="Reason">Safe explanation visible in the selected history view.</param>
/// <param name="OccurredUtc">UTC action instant.</param>
/// <param name="Actor">Persisted actor email and display name, or null when no user label is available, including system actions.</param>
public sealed record QuestHistoryItem(string Action, string Reason, DateTimeOffset OccurredUtc, string? Actor);
