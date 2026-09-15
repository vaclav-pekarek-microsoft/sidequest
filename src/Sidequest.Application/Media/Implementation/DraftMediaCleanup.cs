using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Media.Implementation;

internal static class DraftMediaCleanup
{
    // The caller has already locked the Event and authorized never-published, participation-free deletion.
    internal static async Task StageAsync(ISidequestDbContext db, Quest quest, Guid actor,
        DateTimeOffset now, CancellationToken token)
    {
        var assets = await db.MediaAssets.Where(x => x.QuestId == quest.Id).ToListAsync(token).ConfigureAwait(false);
        foreach (var asset in assets)
        {
            if (asset.BlobName != $"covers/{asset.Id:N}.png")
                throw new DomainException(ErrorCode.Conflict, "Draft media requires operational repair before deletion.");
            // An unfinished provider write must time out before this independent deletion intent can run.
            var due = asset.Status == MediaStatus.Ready ? now :
                (asset.CreatedUtc.AddHours(24) > now ? asset.CreatedUtc.AddHours(24) : now);
            new MediaCleanupPayload(Guid.NewGuid(), asset.Id, quest.Id, quest.EventId, asset.CreatedById,
                asset.CreatedUtc, due, true).Stage(db, actor, now);
        }
        quest.CoverAssetId = null;
        db.MediaAssets.RemoveRange(assets);
    }
}
