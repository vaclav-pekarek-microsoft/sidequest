using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Sidequest.Application.Events.Implementation;
using Sidequest.Application.Media;
using Sidequest.Application.Media.Implementation;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Security;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.IntegrationTests.CoreQuests;
using Sidequest.IntegrationTests.FoundationPersistence;
using Sidequest.Infrastructure.Media;

namespace Sidequest.IntegrationTests.SecondaryMedia;

internal sealed record MediaScenario(SqlTestDatabase Database, FoundationSeed Seed, QuestTestClock Clock)
{
    internal MemoryMediaStorage Storage { get; } = new();
    internal EventLifecycleReconciler Lifecycle { get; } = new(new QuestEventLifecycle(new ChangeWriter()));
    internal MediaService Service(UserAccount? user = null) => new(new QuestTestFactory(Database),
        new ResourceAccess(StubCurrentUser.For(user ?? Seed.User)), new ChangeWriter(), Clock,
        Lifecycle, new SkiaImageSanitizer(), Storage);
    internal QuestService Quests => new(new QuestTestFactory(Database), new ResourceAccess(StubCurrentUser.For(Seed.User)),
        new ChangeWriter(), Clock, Lifecycle);
    internal MediaCleanupHandler Cleanup => new(new QuestTestFactory(Database), Storage, Clock);

    internal static async Task<MediaScenario> CreateAsync(SqlTestDatabase database, bool draft = false, bool privateQuest = false)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await FoundationSeed.PersistAsync(database, seed.Membership(), seed.Membership(seed.Other.Id),
            new QuestOwner { QuestId = seed.Quest.Id, UserId = seed.User.Id },
            new EventOwner { EventId = seed.Event.Id, UserId = seed.Other.Id });
        await using var db = database.CreateContext();
        var quest = await db.Quests.SingleAsync(x => x.Id == seed.Quest.Id);
        quest.Status = draft ? QuestStatus.Draft : QuestStatus.Active;
        quest.Visibility = privateQuest ? QuestVisibility.Private : QuestVisibility.Public;
        await db.SaveChangesAsync();
        return new(database, seed, new QuestTestClock(FoundationSeed.Now.AddHours(-1)));
    }

    internal async Task<string> VersionAsync()
    {
        await using var db = Database.CreateContext();
        return Convert.ToBase64String((await db.Quests.SingleAsync(x => x.Id == Seed.Quest.Id)).Version);
    }

    internal async Task<CoverUpdate> UploadAsync()
    {
        using var input = Image();
        return await Service().UploadCoverAsync(Seed.Quest.Id, await VersionAsync(), input);
    }

    internal static MemoryStream Image()
    {
        using var bitmap = new SKBitmap(3, 2);
        bitmap.Erase(SKColors.Orange);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new MemoryStream(data.ToArray());
    }
}
