using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events;
using Sidequest.Application.Quests.Implementation;
using Sidequest.Application.Shared;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Media.Implementation;

/// <summary>Coordinates authorized cover changes with durable expiry, without holding SQL locks during image or Blob I/O.</summary>
/// <param name="factory">Creates short-lived operation contexts.</param>
/// <param name="access">Rechecks current account and resource permissions.</param>
/// <param name="writer">Stages the existing Quest audiences without calendar withdrawal for cover edits.</param>
/// <param name="clock">Supplies UTC lifecycle, rate and expiry boundaries.</param>
/// <param name="eventLifecycle">Stages overdue Event reconciliation in the Event-first transaction.</param>
/// <param name="sanitizer">Decodes untrusted caller-owned streams.</param>
/// <param name="storage">Performs private provider operations after SQL commits.</param>
public sealed class MediaService(ISidequestDbContextFactory factory, IResourceAccess access,
    IChangeWriter writer, TimeProvider clock, IEventLifecycleReconciler eventLifecycle,
    IImageSanitizer sanitizer, IPrivateMediaStorage storage) : IMediaService
{
    private static readonly SemaphoreSlim UploadSlots = new(2, 2);
    private static readonly SemaphoreSlim ReadSlots = new(2, 2);

    /// <inheritdoc />
    /// <remarks>At most five uploads per account per rolling minute are admitted using a serializable persisted audit check.
    /// Two nonqueued process-wide slots bound the entire upload's buffers, including provider waits.
    /// Processing has a ninety-second cooperative deadline; native codec calls have bounded dimensions.</remarks>
    public async Task<CoverUpdate> UploadCoverAsync(Guid questId, string expectedVersion, Stream content,
        CancellationToken cancellationToken = default)
    {
        if (!await UploadSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new DomainException(ErrorCode.DependencyUnavailable, "Image processing is busy. Try again shortly.");
        try
        {
            return await UploadCoreAsync(questId, expectedVersion, content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UploadSlots.Release();
        }
    }

    private async Task<CoverUpdate> UploadCoreAsync(Guid questId, string expectedVersion, Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var pending = await EditAsync(questId, expectedVersion, async (db, quest, actor, now, token) =>
        {
            var since = now.AddMinutes(-1);
            if (await db.AuditEntries.CountAsync(x => x.ActorId == actor && x.OccurredUtc > since &&
                x.Action.StartsWith("MediaUploadStaged:"), token).ConfigureAwait(false) >= 5)
                throw new DomainException(ErrorCode.Conflict, "Upload limit reached. Wait one minute before trying again.");
            var asset = new MediaAsset
            {
                QuestId = questId, CreatedById = actor, CreatedUtc = now, Status = MediaStatus.Pending
            };
            asset.BlobName = $"covers/{asset.Id:N}.png";
            db.MediaAssets.Add(asset);
            new MediaCleanupPayload(Guid.NewGuid(), asset.Id, questId, quest.EventId, actor,
                now, now.AddHours(24), false).Stage(db, actor, now);
            return asset;
        }, cancellationToken).ConfigureAwait(false);

        // Even uncertain writes have a durable key and expiry before the first provider operation.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var image = await sanitizer.SanitizeAsync(content, deadline.Token).ConfigureAwait(false);
        if (clock.GetUtcNow() >= pending.CreatedUtc.AddHours(24).AddSeconds(-90))
            throw Conflict("The upload has expired. Select the image again.");
        await storage.WriteAsync(pending.BlobName, image.Data, image.ContentType, deadline.Token).ConfigureAwait(false);
        var updated = await EditAsync(questId, expectedVersion, async (db, quest, actor, now, token) =>
        {
            var asset = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == pending.Id, token).ConfigureAwait(false);
            if (asset is null || asset.Status != MediaStatus.Pending || asset.CreatedById != actor ||
                asset.QuestId != quest.Id || asset.CreatedUtc != pending.CreatedUtc ||
                asset.BlobName != pending.BlobName || now >= asset.CreatedUtc.AddHours(24))
                throw Conflict("The upload is no longer available. Select the image again.");
            asset.ContentType = image.ContentType;
            asset.SizeBytes = image.Data.Length;
            asset.Width = image.Width;
            asset.Height = image.Height;
            asset.Status = MediaStatus.Ready;
            quest.CoverAssetId = asset.Id;
            await RecordChangeAsync(db, quest, actor, now, token).ConfigureAwait(false);
            return quest;
        }, deadline.Token).ConfigureAwait(false);
        return new(updated.CoverAssetId, Convert.ToBase64String(updated.Version));
    }

    /// <inheritdoc />
    public async Task<CoverUpdate> RemoveCoverAsync(Guid questId, string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var updated = await EditAsync(questId, expectedVersion, async (db, quest, actor, now, token) =>
        {
            if (quest.CoverAssetId is not null)
            {
                quest.CoverAssetId = null;
                await RecordChangeAsync(db, quest, actor, now, token).ConfigureAwait(false);
            }
            return quest;
        }, cancellationToken).ConfigureAwait(false);
        return new(null, Convert.ToBase64String(updated.Version));
    }

    /// <inheritdoc />
    /// <remarks>Two nonqueued process-wide read slots bound buffers even while a provider or SQL is slow.</remarks>
    public async Task<MediaContent> ReadAsync(Guid assetId, bool moderation = false,
        CancellationToken cancellationToken = default)
    {
        if (!await ReadSlots.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new DomainException(ErrorCode.DependencyUnavailable, "Image delivery is busy. Try again shortly.");
        try
        {
            return await ReadCoreAsync(assetId, moderation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReadSlots.Release();
        }
    }

    private async Task<MediaContent> ReadCoreAsync(Guid assetId, bool moderation, CancellationToken cancellationToken)
    {
        var asset = await AuthorizeReadAsync(assetId, moderation, true, cancellationToken).ConfigureAwait(false);
        if (asset.SizeBytes is <= 0 or > 84_000_000 || asset.ContentType != "image/png" ||
            asset.Width <= 0 || asset.Height <= 0 || (long)asset.Width * asset.Height > 20_000_000 ||
            asset.BlobName != $"covers/{asset.Id:N}.png")
            throw Unavailable();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        await using var input = await storage.OpenReadAsync(asset.BlobName, deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream(checked((int)asset.SizeBytes));
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
            if (read == 0)
                break;
            if (output.Length + read > asset.SizeBytes)
                throw Unavailable();
            await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
        }
        if (output.Length != asset.SizeBytes)
            throw Unavailable();
        var current = await AuthorizeReadAsync(assetId, moderation, false, deadline.Token).ConfigureAwait(false);
        if (current.QuestId != asset.QuestId || current.BlobName != asset.BlobName ||
            current.SizeBytes != asset.SizeBytes || current.ContentType != asset.ContentType)
            throw Unavailable();
        return new(output.ToArray(), asset.ContentType);
    }

    private async Task<MediaAsset> AuthorizeReadAsync(Guid id, bool moderation, bool recordAudit, CancellationToken token)
    {
        await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
        var parent = await (from asset in db.MediaAssets
                            join parentQuest in db.Quests on asset.QuestId equals parentQuest.Id
                            where asset.Id == id
                            select (Guid?)parentQuest.EventId).SingleOrDefaultAsync(token).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: token).ConfigureAwait(false);
        if (parent is not null)
            await db.LockEventAsync(parent.Value, token).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, token).ConfigureAwait(false);
        var item = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == id, token).ConfigureAwait(false)
            ?? throw Unavailable();
        var quest = await RequireQuestAsync(db, item.QuestId, actor.Id, false, moderation, token).ConfigureAwait(false);
        if (item.Status != MediaStatus.Ready || quest.CoverAssetId != item.Id)
            throw Unavailable();
        if (moderation && recordAudit)
            db.AuditEntries.Add(new AuditEntry
            {
                ResourceKind = ResourceKind.Quest, ResourceId = quest.Id, ActorId = actor.Id,
                Action = "ModerationCoverRead", OccurredUtc = clock.GetUtcNow(), CorrelationId = Guid.NewGuid().ToString("N")
            });
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return item;
    }

    private async Task<T> EditAsync<T>(Guid id, string version,
        Func<ISidequestDbContext, Quest, Guid, DateTimeOffset, CancellationToken, Task<T>> mutation,
        CancellationToken token)
    {
        while (true)
        {
            await ReconcileAsync(id, token).ConfigureAwait(false);
            await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
            var parentId = await QuestChanges.ParentIdAsync(db, id, token).ConfigureAwait(false);
            await using var transaction = await db.BeginTransactionAsync(cancellationToken: token).ConfigureAwait(false);
            if (parentId is not null)
                await db.LockEventAsync(parentId.Value, token).ConfigureAwait(false);
            var actor = await access.RequireUserAsync(db, token).ConfigureAwait(false);
            var quest = await RequireQuestAsync(db, id, actor.Id, true, false, token).ConfigureAwait(false);
            var parent = await db.Events.SingleAsync(x => x.Id == quest.EventId, token).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (QuestChanges.ParentIsOverdue(parent, now) ||
                quest.Status is QuestStatus.Active or QuestStatus.Suspended && now >= quest.EndUtc)
                continue;
            InputRules.Version(quest, version);
            QuestChanges.RequireActive(parent, now);
            if (quest.Status is not (QuestStatus.Draft or QuestStatus.Active or QuestStatus.Suspended))
                throw Conflict("This Quest cannot be edited.");
            var result = await mutation(db, quest, actor.Id, now, token).ConfigureAwait(false);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }
    }

    private async Task ReconcileAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
        var parentId = await QuestChanges.ParentIdAsync(db, id, token).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: token).ConfigureAwait(false);
        if (parentId is not null)
            await db.LockEventAsync(parentId.Value, token).ConfigureAwait(false);
        var actor = await access.RequireUserAsync(db, token).ConfigureAwait(false);
        var quest = await RequireQuestAsync(db, id, actor.Id, true, false, token).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        await eventLifecycle.ReconcileAsync(db, quest.EventId, now, token).ConfigureAwait(false);
        if (quest.Status is QuestStatus.Active or QuestStatus.Suspended && now >= quest.EndUtc)
            await QuestChanges.TransitionAsync(db, writer, quest, QuestStatus.Completed, null,
                "The Quest end time has been reached.", now, token).ConfigureAwait(false);
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private async Task<Quest> RequireQuestAsync(ISidequestDbContext db, Guid id, Guid actor,
        bool owner, bool moderation, CancellationToken token)
    {
        var quest = await access.RequireQuestAsync(db, id, actor, owner, moderation, token).ConfigureAwait(false);
        if (!await db.EventMemberships.AnyAsync(x => x.EventId == quest.EventId &&
            x.UserId == actor && x.Status == MembershipStatus.Active, token).ConfigureAwait(false) ||
            await db.Events.AnyAsync(x => x.Id == quest.EventId && x.Status == EventStatus.Draft, token).ConfigureAwait(false))
            throw Unavailable();
        return quest;
    }

    private async Task RecordChangeAsync(ISidequestDbContext db, Quest quest, Guid actor,
        DateTimeOffset now, CancellationToken token)
    {
        var audience = await QuestChanges.CaptureAsync(db, quest.Id, token).ConfigureAwait(false);
        var change = QuestChanges.Audit(db, quest, actor, "CoverChanged", "", now);
        if (quest.Status == QuestStatus.Active)
            QuestChanges.Notify(db, writer, quest, change, actor, NotificationKind.QuestUpdated,
                audience.Owners.Concat(audience.Attendees).Concat(audience.Followers), now,
                previousAttendees: audience.Attendees);
        if (quest.Status == QuestStatus.Suspended)
        {
            var moderators = await db.EventOwners.Where(x => x.EventId == quest.EventId)
                .Select(x => x.UserId).ToArrayAsync(token).ConfigureAwait(false);
            QuestChanges.Notify(db, writer, quest, change, actor, NotificationKind.SuspendedQuestEdited,
                audience.Owners.Concat(moderators), now);
        }
    }

    private static DomainException Conflict(string message) => new(ErrorCode.Conflict, message);
    private static DomainException Unavailable() => new(ErrorCode.NotFound, "This resource is unavailable.");
}
