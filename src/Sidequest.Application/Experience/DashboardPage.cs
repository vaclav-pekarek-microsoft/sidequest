using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;

namespace Sidequest.Application.Experience;

/// <summary>One authorized dashboard page with private invitation totals disclosed only for currently owned Quests.</summary>
/// <param name="Quests">The existing service's ordered, filtered page, including attendance and follower counts.</param>
/// <param name="InvitedCounts">Active eligible member invitations keyed by private Quest ID; absence means withheld, not zero.</param>
public sealed record DashboardPage(PageResult<QuestSummary> Quests, IReadOnlyDictionary<Guid, int> InvitedCounts);
