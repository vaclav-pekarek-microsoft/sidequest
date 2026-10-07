using Sidequest.Application.Abstractions;
using Sidequest.Application.Experience;
using Sidequest.Application.Quests;
using Sidequest.Domain.Model;

namespace Sidequest.UnitTests.SecondaryExperience;

/// <summary>Checks complete home aggregation across the authorized Quest service's bounded pages.</summary>
public sealed class QuestHomeServiceTests
{
    /// <summary>The projection reads page size one hundred until the authoritative total is reached for both categories.</summary>
    /// <returns>Completion after exact request and aggregate-count assertions.</returns>
    [Fact]
    public async Task ProjectionReadsEveryAuthorizedPageInBoundedBatches()
    {
        var now = new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero);
        var visible = Enumerable.Range(0, 101)
            .Select(index => Summary(now, index))
            .ToArray();
        var requests = new List<(QuestListKind Kind, int Page, int Size)>();
        var quests = SnapshotServiceProxy.Create<IQuestService>((method, arguments) =>
        {
            Assert.Equal(nameof(IQuestService.ListAsync), method.Name);
            var kind = (QuestListKind)arguments![0]!;
            var request = (PageRequest)arguments[2]!;
            requests.Add((kind, request.Page, request.PageSize));
            if (kind == QuestListKind.Invited)
                return Task.FromResult(new PageResult<QuestSummary>([], 0, request.Page, request.PageSize));
            var items = visible.Skip(request.Offset).Take(request.Limit).ToArray();
            return Task.FromResult(new PageResult<QuestSummary>(
                items, visible.Length, request.Page, request.PageSize));
        });

        var result = await new QuestHomeService(quests, new FixedClock(now)).GetAsync();

        Assert.Equal(101, result.UpcomingTotalCount);
        Assert.Equal(101, result.UpcomingJoinedCount);
        Assert.Equal(new[]
        {
            (QuestListKind.Board, 1, 100),
            (QuestListKind.Board, 2, 100),
            (QuestListKind.Invited, 1, 100)
        }, requests);
    }

    private static QuestSummary Summary(DateTimeOffset now, int index) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Event", $"Quest {index}", "Room",
            now.AddHours(index + 1), now.AddHours(index + 2), "Etc/UTC",
            QuestStatus.Active, QuestVisibility.Public, 0, 0, null,
            ParticipationStatus.Joined, false, false, "", null);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => now;
    }
}
