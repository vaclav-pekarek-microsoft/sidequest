using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryMedia;

/// <summary>Verifies durable cleanup identity, immutable expiry, retry safety and authorized Draft deletion on migrated SQL.</summary>
/// <param name="database">Fixture-owned isolated database used only by this test class.</param>
[Collection("SecondaryMedia")]
public sealed class MediaCleanupTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Early claims remain retryable, while mutable queue due times cannot change the immutable 24-hour expiry.</summary>
    /// <returns>Completion after exact before/at-expiry behavior and repeat-safe cleanup.</returns>
    [Fact]
    public async Task Expiry_RejectsEarlyClaimThenDeletesAtCapturedCutoffDespiteRetryDue()
    {
        var scenario = await PendingAsync();
        var job = await JobAsync(scenario);
        var created = scenario.Clock.Now;
        await using (var edit = database.CreateContext())
        {
            var work = await edit.ScheduledWork.SingleAsync(x => x.Id == job.Id);
            work.DueUtc = created.AddDays(3);
            work.Attempts = 3;
            work.LeaseId = Guid.NewGuid();
            work.Status = WorkStatus.Processing;
            await edit.SaveChangesAsync();
        }
        scenario.Clock.Now = created.AddHours(24).AddTicks(-1);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Cleanup.ExecuteAsync(job.Id, default))).Code);
        Assert.Equal(0, scenario.Storage.Deletes);
        scenario.Clock.Now = created.AddHours(24);
        await scenario.Cleanup.ExecuteAsync(job.Id, default);
        await scenario.Cleanup.ExecuteAsync(job.Id, default);
        Assert.Equal(1, scenario.Storage.Deletes);
        await using var db = database.CreateContext();
        Assert.False(await db.MediaAssets.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
        var unchanged = await db.ScheduledWork.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(3, unchanged.Attempts);
        Assert.Equal(WorkStatus.Processing, unchanged.Status);
        Assert.Equal(created.AddDays(3), unchanged.DueUtc);
        Assert.NotNull(unchanged.LeaseId);
        Assert.Single(await db.AuditEntries.Where(x => x.ResourceId == scenario.Seed.Quest.Id && x.Action == "MediaCleanupCompleted").ToListAsync());
    }

    /// <summary>Provider failure preserves Failed metadata and its durable key so retry removes the same object.</summary>
    /// <returns>Completion after safe retry and no accidental ready publication.</returns>
    [Fact]
    public async Task ExpiryProviderFailure_RetainsMetadataUntilSuccessfulRetry()
    {
        var scenario = await PendingAsync();
        var job = await JobAsync(scenario);
        await using (var read = database.CreateContext())
        {
            var asset = await read.MediaAssets.SingleAsync(x => x.QuestId == scenario.Seed.Quest.Id);
            Assert.True(scenario.Storage.Blobs.TryAdd(asset.BlobName, [1, 2]));
        }
        scenario.Clock.Now = scenario.Clock.Now.AddHours(24);
        scenario.Storage.BeforeDelete = () => throw new DomainException(ErrorCode.DependencyUnavailable, "Synthetic provider failure.");
        Assert.Equal(ErrorCode.DependencyUnavailable, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Cleanup.ExecuteAsync(job.Id, default))).Code);
        await using (var read = database.CreateContext())
        {
            Assert.Equal(MediaStatus.Failed, (await read.MediaAssets.SingleAsync(x => x.QuestId == scenario.Seed.Quest.Id)).Status);
            Assert.False(await read.AuditEntries.AnyAsync(x => x.ResourceId == scenario.Seed.Quest.Id && x.Action == "MediaCleanupCompleted"));
        }
        Assert.Single(scenario.Storage.Blobs);
        scenario.Storage.BeforeDelete = null;
        await scenario.Cleanup.ExecuteAsync(job.Id, default);
        Assert.Empty(scenario.Storage.Blobs);
        await using var db = database.CreateContext();
        Assert.False(await db.MediaAssets.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
    }

    /// <summary>Payload, row and immutable audit identity tampering cannot redirect deletion to a different key or expiry.</summary>
    /// <param name="field">The payload or work-row identity changed without its immutable staging audit.</param>
    /// <returns>Completion after permanent validation rejection with no provider deletion.</returns>
    [Theory]
    [InlineData("AssetId")]
    [InlineData("WorkId")]
    [InlineData("EventId")]
    [InlineData("CreatedUtc")]
    [InlineData("ExpiresUtc")]
    [InlineData("DraftDeletion")]
    [InlineData("QuestRow")]
    [InlineData("UserRow")]
    [InlineData("KeyRow")]
    [InlineData("TypeRow")]
    [InlineData("Malformed")]
    [InlineData("DateOverflow")]
    public async Task TamperedIdentity_NeverCallsDelete(string field)
    {
        var scenario = await PendingAsync();
        var job = await JobAsync(scenario);
        await using (var edit = database.CreateContext())
        {
            var work = await edit.ScheduledWork.SingleAsync(x => x.Id == job.Id);
            var payload = JsonNode.Parse(work.PayloadJson)!;
            switch (field)
            {
                case "QuestRow": work.QuestId = Guid.NewGuid(); break;
                case "UserRow": work.UserId = Guid.NewGuid(); break;
                case "KeyRow": work.DeduplicationKey = "different-key"; break;
                case "TypeRow": work.Type = WorkTypes.QuestCompletion; break;
                case "Malformed": work.PayloadJson = "{"; break;
                case "DateOverflow":
                    payload["CreatedUtc"] = DateTimeOffset.MaxValue;
                    payload["ExpiresUtc"] = DateTimeOffset.MaxValue;
                    work.PayloadJson = payload.ToJsonString();
                    break;
                default:
                    payload[field] = field switch
                    {
                        "CreatedUtc" or "ExpiresUtc" => JsonValue.Create(scenario.Clock.Now.AddDays(2)),
                        "DraftDeletion" => JsonValue.Create(true),
                        _ => JsonValue.Create(Guid.NewGuid())
                    };
                    work.PayloadJson = payload.ToJsonString();
                    break;
            }
            await edit.SaveChangesAsync();
        }
        scenario.Clock.Now = scenario.Clock.Now.AddDays(3);
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Cleanup.ExecuteAsync(job.Id, default));
        Assert.Equal(ErrorCode.Validation, failure.Code);
        Assert.Equal(0, scenario.Storage.Deletes);
        await using var db = database.CreateContext();
        Assert.Single(await db.MediaAssets.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync());
    }

    /// <summary>Expiry never applies unapproved retention to attached or detached Ready images.</summary>
    /// <param name="detach">Whether the owner first removes the current assignment.</param>
    /// <returns>Completion after ready asset and private object preservation.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyMedia_IsRetainedUnderGeneralExpiry(bool detach)
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        var result = await scenario.UploadAsync();
        var job = await JobAsync(scenario);
        if (detach)
            await scenario.Service().RemoveCoverAsync(scenario.Seed.Quest.Id, result.Version);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(2);
        await scenario.Cleanup.ExecuteAsync(job.Id, default);
        await using var db = database.CreateContext();
        Assert.Equal(MediaStatus.Ready, (await db.MediaAssets.SingleAsync(x => x.Id == result.AssetId)).Status);
        Assert.Single(scenario.Storage.Blobs);
        Assert.Equal(0, scenario.Storage.Deletes);
    }

    /// <summary>Authorized unpublished Draft deletion stages new cleanup for current and historical covers even after original jobs finished.</summary>
    /// <returns>Completion after metadata deletion, durable retry across Blob failure and retained explicit deletion audit.</returns>
    [Fact]
    public async Task DeleteDraft_WithHistoricalReadyCovers_LeavesAuditedIndependentRetryIntent()
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        await scenario.UploadAsync();
        await scenario.UploadAsync();
        await using (var read = database.CreateContext())
        {
            foreach (var work in await read.ScheduledWork.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync())
            {
                await scenario.Cleanup.ExecuteAsync(work.Id, default);
                work.Status = WorkStatus.Completed;
            }
            await read.SaveChangesAsync();
        }
        await scenario.Quests.DeleteDraftAsync(scenario.Seed.Quest.Id, await scenario.VersionAsync());
        List<ScheduledWork> deletion;
        await using (var read = database.CreateContext())
        {
            Assert.False(await read.Quests.AnyAsync(x => x.Id == scenario.Seed.Quest.Id));
            Assert.False(await read.MediaAssets.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
            Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == scenario.Seed.Quest.Id && x.Action == "DraftDeleted").ToListAsync());
            deletion = await read.ScheduledWork.Where(x => x.QuestId == scenario.Seed.Quest.Id &&
                x.DeduplicationKey.StartsWith("media:draft:")).ToListAsync();
            Assert.Equal(2, deletion.Count);
            Assert.Equal(2, await read.ScheduledWork.CountAsync(x => x.QuestId == scenario.Seed.Quest.Id &&
                x.DeduplicationKey.StartsWith("media:expiry:") && x.Status == WorkStatus.Completed));
        }
        scenario.Storage.BeforeDelete = () => throw new DomainException(ErrorCode.DependencyUnavailable, "Synthetic unavailable storage.");
        await Assert.ThrowsAsync<DomainException>(() => scenario.Cleanup.ExecuteAsync(deletion[0].Id, default));
        Assert.Equal(2, scenario.Storage.Blobs.Count);
        scenario.Storage.BeforeDelete = null;
        foreach (var work in deletion)
        {
            await scenario.Cleanup.ExecuteAsync(work.Id, default);
            await scenario.Cleanup.ExecuteAsync(work.Id, default);
        }
        Assert.Empty(scenario.Storage.Blobs);
    }

    /// <summary>Deletion during upload preserves a delayed, independently audited key after the Quest and asset rows disappear.</summary>
    /// <returns>Completion after failed final attachment, early cleanup conflict and eventual object reclamation.</returns>
    [Fact]
    public async Task DeleteDraft_DuringProviderWrite_PreservesPendingKeyUntilExpiry()
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        scenario.Storage.AfterWrite = async () =>
            await scenario.Quests.DeleteDraftAsync(scenario.Seed.Quest.Id, await scenario.VersionAsync());
        using var input = MediaScenario.Image();
        var version = await scenario.VersionAsync();
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, input))).Code);
        var job = await JobAsync(scenario, draft: true);
        Assert.Single(scenario.Storage.Blobs);
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() => scenario.Cleanup.ExecuteAsync(job.Id, default))).Code);
        scenario.Clock.Now = scenario.Clock.Now.AddHours(24);
        await scenario.Cleanup.ExecuteAsync(job.Id, default);
        Assert.Empty(scenario.Storage.Blobs);
        var expiry = await JobAsync(scenario);
        await scenario.Cleanup.ExecuteAsync(expiry.Id, default);
    }

    /// <summary>Current ownership, individual membership, never-published history and participation guards remain enforced with media.</summary>
    /// <param name="guard">The independent deletion guard intentionally violated.</param>
    /// <returns>Completion after rejection and unchanged Quest/media persistence.</returns>
    [Theory]
    [InlineData("participation")]
    [InlineData("published")]
    [InlineData("membership")]
    [InlineData("owner")]
    public async Task DeleteDraft_DoesNotWeakenExistingGuards(string guard)
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        var cover = await scenario.UploadAsync();
        await using (var edit = database.CreateContext())
        {
            if (guard == "participation")
                edit.Participations.Add(new QuestParticipation { QuestId = scenario.Seed.Quest.Id, UserId = scenario.Seed.User.Id, Status = ParticipationStatus.None });
            if (guard == "published")
                edit.QuestStatusHistory.Add(new QuestStatusHistory
                {
                    QuestId = scenario.Seed.Quest.Id, Previous = QuestStatus.Draft, Next = QuestStatus.Active, OccurredUtc = scenario.Clock.Now
                });
            if (guard == "membership")
                (await edit.EventMemberships.SingleAsync(x => x.EventId == scenario.Seed.Event.Id && x.UserId == scenario.Seed.User.Id)).Status = MembershipStatus.Removed;
            if (guard == "owner")
                edit.QuestOwners.Remove(await edit.QuestOwners.SingleAsync(x => x.QuestId == scenario.Seed.Quest.Id));
            await edit.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<DomainException>(() => scenario.Quests.DeleteDraftAsync(scenario.Seed.Quest.Id, cover.Version));
        Assert.Contains(error.Code, new[] { ErrorCode.Conflict, ErrorCode.NotFound });
        await using var db = database.CreateContext();
        Assert.True(await db.Quests.AnyAsync(x => x.Id == scenario.Seed.Quest.Id));
        Assert.True(await db.MediaAssets.AnyAsync(x => x.Id == cover.AssetId));
        Assert.False(await db.ScheduledWork.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id && x.DeduplicationKey.StartsWith("media:draft:")));
    }

    /// <summary>Missing asset metadata alone is never interpreted as authorization to delete a Blob.</summary>
    /// <returns>Completion after an inconsistent row removal is rejected without provider I/O.</returns>
    [Fact]
    public async Task MissingMetadataWithoutDeletionAudit_DoesNotAuthorizeCleanup()
    {
        var scenario = await PendingAsync();
        var job = await JobAsync(scenario);
        await using (var edit = database.CreateContext())
        {
            edit.MediaAssets.Remove(await edit.MediaAssets.SingleAsync(x => x.QuestId == scenario.Seed.Quest.Id));
            await edit.SaveChangesAsync();
        }
        scenario.Clock.Now = scenario.Clock.Now.AddHours(24);
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Cleanup.ExecuteAsync(job.Id, default));
        Assert.Equal(ErrorCode.Validation, failure.Code);
        Assert.Equal(0, scenario.Storage.Deletes);
    }

    /// <summary>A real authorized Draft-deletion audit cannot be reused to target a different asset after resource metadata is gone.</summary>
    /// <returns>Completion after immutable per-asset intent rejects a retargeted deletion payload.</returns>
    [Fact]
    public async Task DeletedDraftIntent_CannotBeRetargetedAfterMetadataRemoval()
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        var cover = await scenario.UploadAsync();
        await scenario.Quests.DeleteDraftAsync(scenario.Seed.Quest.Id, cover.Version);
        var job = await JobAsync(scenario, draft: true);
        await using (var edit = database.CreateContext())
        {
            var work = await edit.ScheduledWork.SingleAsync(x => x.Id == job.Id);
            var payload = JsonNode.Parse(work.PayloadJson)!;
            var replacement = Guid.NewGuid();
            payload["AssetId"] = replacement;
            work.PayloadJson = payload.ToJsonString();
            work.DeduplicationKey = $"media:draft:{replacement:N}:{work.Id:N}";
            await edit.SaveChangesAsync();
        }
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Cleanup.ExecuteAsync(job.Id, default));
        Assert.Equal(ErrorCode.Validation, failure.Code);
        Assert.Equal(0, scenario.Storage.Deletes);
        Assert.Single(scenario.Storage.Blobs);
    }

    private async Task<MediaScenario> PendingAsync()
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        using var invalid = new MemoryStream([1, 2, 3]);
        var version = await scenario.VersionAsync();
        Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, invalid))).Code);
        return scenario;
    }

    private async Task<ScheduledWork> JobAsync(MediaScenario scenario, bool draft = false)
    {
        await using var db = database.CreateContext();
        var prefix = draft ? "media:draft:" : "media:expiry:";
        return await db.ScheduledWork.SingleAsync(x => x.QuestId == scenario.Seed.Quest.Id && x.DeduplicationKey.StartsWith(prefix));
    }
}
