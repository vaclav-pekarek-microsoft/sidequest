using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.Application.Media.Implementation;

/// <summary>Reclaims expired temporary uploads and explicitly deleted Draft covers using immutable audited intent.</summary>
/// <param name="factory">Creates isolated SQL contexts; the queue alone owns leases, attempts and retry scheduling.</param>
/// <param name="storage">Deletes private objects idempotently outside SQL transactions.</param>
/// <param name="clock">Supplies the authoritative immutable-expiry comparison.</param>
public sealed class MediaCleanupHandler(ISidequestDbContextFactory factory, IPrivateMediaStorage storage,
    TimeProvider clock) : IBackgroundWorkHandler
{
    /// <inheritdoc />
    public string WorkType => WorkTypes.MediaCleanup;

    /// <inheritdoc />
    /// <remarks>A still-current early expiry fails retryably. Ready historical covers are retained unless an
    /// authorized unpublished-Draft deletion recorded a separate immutable intent and removed their metadata.</remarks>
    public async Task ExecuteAsync(Guid workId, CancellationToken cancellationToken)
    {
        var payload = await LoadAsync(workId, cancellationToken).ConfigureAwait(false);
        if (!await PrepareAsync(payload, cancellationToken).ConfigureAwait(false))
            return;
        await storage.DeleteIfExistsAsync(payload.BlobName, cancellationToken).ConfigureAwait(false);
        await using var db = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.LockEventAsync(payload.EventId, cancellationToken).ConfigureAwait(false);
        await ValidateAuditAsync(db, payload, cancellationToken).ConfigureAwait(false);
        var asset = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == payload.AssetId, cancellationToken).ConfigureAwait(false);
        if (asset is not null)
        {
            ValidateAsset(asset, payload);
            if (payload.DraftDeletion || asset.Status != MediaStatus.Failed ||
                await db.Quests.AnyAsync(x => x.CoverAssetId == asset.Id, cancellationToken).ConfigureAwait(false))
                throw Invalid();
            db.MediaAssets.Remove(asset);
        }
        if (!await CompletedAsync(db, payload, cancellationToken).ConfigureAwait(false))
            db.AuditEntries.Add(new AuditEntry
            {
                ResourceKind = ResourceKind.Quest, ResourceId = payload.QuestId, Action = "MediaCleanupCompleted",
                CorrelationId = payload.WorkId.ToString("N"), Reason = payload.Hash, OccurredUtc = clock.GetUtcNow()
            });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MediaCleanupPayload> LoadAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
        var work = await db.ScheduledWork.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token).ConfigureAwait(false)
            ?? throw Invalid();
        MediaCleanupPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<MediaCleanupPayload>(work.PayloadJson) ?? throw Invalid();
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        if (work.Type != WorkType || payload.WorkId != id || payload.AssetId == Guid.Empty ||
            payload.QuestId == Guid.Empty || payload.EventId == Guid.Empty || payload.CreatorId == Guid.Empty ||
            payload.CreatedUtc == default || payload.CreatedUtc > DateTimeOffset.MaxValue.AddHours(-24) ||
            payload.ExpiresUtc < payload.CreatedUtc ||
            !payload.DraftDeletion && payload.ExpiresUtc != payload.CreatedUtc.AddHours(24) ||
            work.QuestId != payload.QuestId || work.UserId != payload.CreatorId || work.DeduplicationKey != payload.Key)
            throw Invalid();
        return payload;
    }

    private async Task<bool> PrepareAsync(MediaCleanupPayload payload, CancellationToken token)
    {
        await using var db = await factory.CreateAsync(token).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken: token).ConfigureAwait(false);
        await db.LockEventAsync(payload.EventId, token).ConfigureAwait(false);
        await ValidateAuditAsync(db, payload, token).ConfigureAwait(false);
        if (await CompletedAsync(db, payload, token).ConfigureAwait(false))
            return false;
        var asset = await db.MediaAssets.SingleOrDefaultAsync(x => x.Id == payload.AssetId, token).ConfigureAwait(false);
        if (payload.DraftDeletion)
        {
            if (asset is not null || await db.Quests.AnyAsync(x => x.Id == payload.QuestId ||
                x.CoverAssetId == payload.AssetId, token).ConfigureAwait(false) ||
                !await db.AuditEntries.AnyAsync(x => x.ResourceKind == ResourceKind.Quest &&
                    x.ResourceId == payload.QuestId && x.Action == "DraftDeleted" && x.ActorId != null, token).ConfigureAwait(false))
                throw Invalid();
        }
        else
        {
            if (asset is null)
            {
                // Explicit per-asset Draft intent, not absence alone, transfers responsibility to that durable job.
                if (await db.AuditEntries.AnyAsync(x => x.ResourceKind == ResourceKind.Quest &&
                    x.ResourceId == payload.QuestId && x.Action == $"DraftMediaDeletion:{payload.AssetId:N}" &&
                    x.ActorId != null, token).ConfigureAwait(false))
                    return false;
                throw Invalid();
            }
            ValidateAsset(asset, payload);
            if (asset.Status == MediaStatus.Ready ||
                await db.Quests.AnyAsync(x => x.CoverAssetId == asset.Id, token).ConfigureAwait(false))
                return false;
            if (asset.Status is not (MediaStatus.Pending or MediaStatus.Failed))
                throw Invalid();
        }
        if (clock.GetUtcNow() < payload.ExpiresUtc)
            throw new DomainException(ErrorCode.Conflict, "Media cleanup is not due yet.");
        if (asset is not null)
            asset.Status = MediaStatus.Failed;
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    private static async Task ValidateAuditAsync(ISidequestDbContext db, MediaCleanupPayload payload, CancellationToken token)
    {
        if (!await db.AuditEntries.AnyAsync(x => x.ResourceKind == ResourceKind.Quest && x.ResourceId == payload.QuestId &&
            x.Action == payload.Action && x.CorrelationId == payload.WorkId.ToString("N") &&
            x.Reason == payload.Hash && x.ActorId != null, token).ConfigureAwait(false))
            throw Invalid();
    }

    private static Task<bool> CompletedAsync(ISidequestDbContext db, MediaCleanupPayload payload, CancellationToken token) =>
        db.AuditEntries.AnyAsync(x => x.ResourceKind == ResourceKind.Quest && x.ResourceId == payload.QuestId &&
            x.Action == "MediaCleanupCompleted" && x.CorrelationId == payload.WorkId.ToString("N") &&
            x.Reason == payload.Hash, token);

    private static void ValidateAsset(MediaAsset asset, MediaCleanupPayload payload)
    {
        if (asset.QuestId != payload.QuestId || asset.CreatedById != payload.CreatorId ||
            asset.CreatedUtc != payload.CreatedUtc || asset.BlobName != payload.BlobName)
            throw Invalid();
    }

    private static DomainException Invalid() => new(ErrorCode.Validation, "Invalid media cleanup intent; operational review required.");
}
