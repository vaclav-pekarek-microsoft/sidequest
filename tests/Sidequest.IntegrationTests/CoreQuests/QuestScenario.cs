using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Quests;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreQuests;

internal sealed record QuestScenario(SqlTestDatabase Database, FoundationSeed Seed, QuestTestClock Clock)
{
    internal QuestTestEventReconciler Reconciler { get; } = new();

    internal QuestService Service(UserAccount? user = null) =>
        new(new QuestTestFactory(Database), new ResourceAccess(StubCurrentUser.For(user ?? Seed.User)), new ChangeWriter(), Clock, Reconciler);

    internal static async Task<QuestScenario> CreateAsync(SqlTestDatabase database, bool privateQuest = false)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await FoundationSeed.PersistAsync(database, seed.Membership(), seed.Membership(seed.Other.Id),
            new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id },
            new EventOwner { EventId = seed.Event.Id, UserId = seed.Other.Id });
        if (privateQuest)
        {
            await using var db = database.CreateContext();
            var quest = await db.Quests.SingleAsync(x => x.Id == seed.Quest.Id);
            quest.Visibility = QuestVisibility.Private;
            await db.SaveChangesAsync();
        }
        return new(database, seed, new QuestTestClock(FoundationSeed.Now.AddHours(-1)));
    }

    internal QuestInput Input(QuestVisibility visibility = QuestVisibility.Public) =>
        new("Synthetic Quest", "Plain description", "Room 1", 1,
            new DateTime(2026, 7, 15, 12, 0, 0), new DateTime(2026, 7, 15, 14, 0, 0), null, null, visibility);
}
