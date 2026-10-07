using Sidequest.Application.Abstractions;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Experience;

/// <summary>Builds the unfiltered signed-in home projection from existing authorized Quest queries.</summary>
/// <param name="quests">Authoritative Quest query service that applies visibility, membership, and eligibility rules.</param>
/// <param name="clock">Provides the single UTC instant used to classify current, upcoming, and past Quests.</param>
public sealed class QuestHomeService(IQuestService quests, TimeProvider clock)
{
    private const int QueryPageSize = 100;
    private const int TopQuestCount = 3;

    /// <summary>Loads all authorized home categories without exposing filter or dashboard-navigation state.</summary>
    /// <param name="cancellationToken">Cancels authorized Quest page reads.</param>
    /// <returns>Statistics, current Quests, top upcoming participation, and active private invitations.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled.</exception>
    public async Task<QuestHomeData> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var board = await ReadAllAsync(QuestListKind.Board, cancellationToken).ConfigureAwait(false);
        var invited = await ReadAllAsync(QuestListKind.Invited, cancellationToken).ConfigureAwait(false);
        var ordered = board.OrderBy(quest => quest.StartUtc).ThenBy(quest => quest.Id).ToArray();
        var upcoming = ordered
            .Where(quest => quest.Status == QuestStatus.Active && quest.StartUtc > now)
            .ToArray();
        var active = ordered
            .Where(quest => quest.Status == QuestStatus.Active && quest.StartUtc <= now && quest.EndUtc > now)
            .ToArray();
        var invitations = invited
            .Where(quest => quest.Status == QuestStatus.Active && quest.EndUtc > now &&
                quest.Participation != ParticipationStatus.Joined)
            .OrderBy(quest => quest.StartUtc)
            .ThenBy(quest => quest.Id)
            .ToArray();

        return new(
            upcoming.Count(quest => quest.Participation == ParticipationStatus.Joined),
            upcoming.Length,
            active.Length,
            ordered.Count(quest => quest.EndUtc <= now),
            active,
            upcoming.Where(quest => quest.Participation == ParticipationStatus.Joined).Take(TopQuestCount).ToArray(),
            upcoming.Where(quest => quest.Participation == ParticipationStatus.Following).Take(TopQuestCount).ToArray(),
            invitations);
    }

    private async Task<IReadOnlyList<QuestSummary>> ReadAllAsync(
        QuestListKind kind, CancellationToken cancellationToken)
    {
        var items = new List<QuestSummary>();
        for (var page = 1; ; page++)
        {
            var result = await quests.ListAsync(
                kind, null, new PageRequest(page, QueryPageSize), cancellationToken).ConfigureAwait(false);
            items.AddRange(result.Items);
            if (items.Count >= result.TotalCount || result.Items.Count == 0)
                return items;
        }
    }
}
