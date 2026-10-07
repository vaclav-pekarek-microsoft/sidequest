using Sidequest.Application.Quests;

namespace Sidequest.Application.Experience;

/// <summary>Authorized, unfiltered home-page summary derived from the current actor's visible Quests.</summary>
/// <param name="UpcomingJoinedCount">Number of future Active Quests the actor has joined.</param>
/// <param name="UpcomingTotalCount">Number of future Active Quests visible to the actor.</param>
/// <param name="ActiveCount">Number of visible Active Quests happening at the projection instant.</param>
/// <param name="PastCount">Number of visible Quests whose end instant has passed.</param>
/// <param name="ActiveQuests">All visible Active Quests happening at the projection instant, ordered by start and identifier.</param>
/// <param name="UpcomingJoined">The first three future Active joined Quests.</param>
/// <param name="UpcomingFollowing">The first three future Active followed Quests.</param>
/// <param name="Invitations">Current private Quest invitations not already joined, ordered by start and identifier.</param>
public sealed record QuestHomeData(
    int UpcomingJoinedCount,
    int UpcomingTotalCount,
    int ActiveCount,
    int PastCount,
    IReadOnlyList<QuestSummary> ActiveQuests,
    IReadOnlyList<QuestSummary> UpcomingJoined,
    IReadOnlyList<QuestSummary> UpcomingFollowing,
    IReadOnlyList<QuestSummary> Invitations);
