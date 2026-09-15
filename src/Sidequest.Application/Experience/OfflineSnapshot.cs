using Sidequest.Application.Quests;

namespace Sidequest.Application.Experience;

/// <summary>Allowlisted device snapshot from a complete, currently authorized Joined query; never an authorization credential.</summary>
/// <param name="AccountId">Internal last signed-in account identifier, without names or contacts.</param>
/// <param name="RefreshedUtc">UTC instant after the full authorized query succeeded; failures never advance this instant.</param>
/// <param name="Quests">Only the existing minimal joined-Quest fields, with no descriptions, images or rosters.</param>
public sealed record OfflineSnapshot(Guid AccountId, DateTimeOffset RefreshedUtc, IReadOnlyList<OfflineQuest> Quests);
