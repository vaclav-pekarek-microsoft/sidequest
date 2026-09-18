namespace Sidequest.Application.Quests;

/// <summary>Names-only identity disclosed through an authorized Quest attendee, follower, or invitation roster without contact information.</summary>
/// <param name="Id">Internal account identifier used only for stable identity and hidden keys, never as a visible label.</param>
/// <param name="DisplayName">Persisted display name; roster access does not authorize disclosure of the email address.</param>
public sealed record QuestRosterPersonSummary(Guid Id, string DisplayName);
