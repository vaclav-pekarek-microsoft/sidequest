using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;

namespace Sidequest.Application.Media.Implementation;

internal sealed record MediaCleanupPayload(Guid WorkId, Guid AssetId, Guid QuestId, Guid EventId,
    Guid CreatorId, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, bool DraftDeletion)
{
    internal string BlobName => $"covers/{AssetId:N}.png";
    internal string Key => $"media:{(DraftDeletion ? "draft" : "expiry")}:{AssetId:N}:{WorkId:N}";
    internal string Action => $"{(DraftDeletion ? "DraftMediaDeletion" : "MediaUploadStaged")}:{AssetId:N}";
    internal string Json => JsonSerializer.Serialize(this);
    internal string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json)));

    internal void Stage(ISidequestDbContext db, Guid actor, DateTimeOffset now)
    {
        db.ScheduledWork.Add(new ScheduledWork
        {
            Id = WorkId, Type = WorkTypes.MediaCleanup, QuestId = QuestId, UserId = CreatorId,
            DeduplicationKey = Key, DueUtc = ExpiresUtc, PayloadJson = Json
        });
        db.AuditEntries.Add(new AuditEntry
        {
            ResourceKind = ResourceKind.Quest, ResourceId = QuestId, ActorId = actor,
            Action = Action, Reason = Hash, CorrelationId = WorkId.ToString("N"), OccurredUtc = now
        });
    }
}
