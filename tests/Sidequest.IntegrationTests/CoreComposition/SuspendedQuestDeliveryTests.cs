using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.IntegrationTests.CoreComposition;

/// <summary>Verifies retained legacy Suspended Quest completion without reintroducing moderation transitions.</summary>
public sealed class SuspendedQuestDeliveryTests
{
    /// <summary>An owner read at the exclusive end reconciles a legacy Suspended record to Completed and preserves history.</summary>
    /// <returns>A task completing after persisted lifecycle and system-history assertions.</returns>
    [Fact]
    public async Task LegacySuspendedQuest_AtEnd_CompletesWithSystemHistory()
    {
        await using var scenario = await CoreCompositionScenario.CreateAsync();
        var owner = await scenario.AddUserAsync("legacy-owner");
        var id = await scenario.CreateQuestAsync(owner);
        await using (var seed = scenario.Read())
        {
            (await seed.Quests.SingleAsync(quest => quest.Id == id)).Status = QuestStatus.Suspended;
            await seed.SaveChangesAsync();
        }
        var persisted = await scenario.QuestAsync(id);
        scenario.Clock.Now = persisted.EndUtc;
        Guid workId;
        await using (var scheduled = scenario.Read())
            workId = await scheduled.ScheduledWork.Where(work => work.QuestId == id &&
                work.Type == WorkTypes.QuestCompletion).Select(work => work.Id).SingleAsync();

        await scenario.Handlers.Single(handler => handler.WorkType == WorkTypes.QuestCompletion)
            .ExecuteAsync(workId, default);
        scenario.ActAs(owner);
        var detail = await scenario.Quests.GetAsync(id);

        Assert.Equal(QuestStatus.Completed, detail.Summary.Status);
        await using var read = scenario.Read();
        Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync(quest => quest.Id == id)).Status);
        var history = Assert.Single(await read.QuestStatusHistory.Where(item => item.QuestId == id &&
            item.Next == QuestStatus.Completed).ToArrayAsync());
        Assert.Equal(QuestStatus.Suspended, history.Previous);
        Assert.Equal(QuestStatus.Completed, history.Next);
        Assert.Null(history.ActorId);
        Assert.Equal("The Quest end time has been reached.", history.Reason);
        Assert.Equal(persisted.EndUtc, history.OccurredUtc);
    }
}
