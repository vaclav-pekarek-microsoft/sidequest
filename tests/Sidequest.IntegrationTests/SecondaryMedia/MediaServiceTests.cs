using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.SecondaryMedia;

/// <summary>Checks actual media use cases, access rules and optimistic concurrency against uniquely migrated SQL.</summary>
/// <param name="database">Fixture-owned SidequestTests GUID catalog; no shared development database is used.</param>
[Collection("SecondaryMedia")]
public sealed class MediaServiceTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Replacement and repeat-safe removal retain old ready assets and never change or withdraw calendar intent.</summary>
    /// <returns>Completion after ready metadata, version, audience, exact pixels and retention assertions.</returns>
    [Fact]
    public async Task UploadReplaceRemove_RetainsReadyHistoryWithoutCalendarChange()
    {
        var scenario = await MediaScenario.CreateAsync(database);
        var initialVersion = await scenario.VersionAsync();
        var first = await scenario.UploadAsync();
        var bytes = await scenario.Service().ReadAsync(first.AssetId!.Value);
        using var decoded = SkiaSharp.SKBitmap.Decode(bytes.Data);
        Assert.Equal(SkiaSharp.SKColors.Orange, decoded.GetPixel(0, 0));
        Assert.Equal("image/png", bytes.ContentType);
        Assert.NotEqual(initialVersion, first.Version);
        var second = await scenario.UploadAsync();
        Assert.NotEqual(first.AssetId, second.AssetId);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().ReadAsync(first.AssetId.Value))).Code);
        var removed = await scenario.Service().RemoveCoverAsync(scenario.Seed.Quest.Id, second.Version);
        Assert.Null(removed.AssetId);
        var repeated = await scenario.Service().RemoveCoverAsync(scenario.Seed.Quest.Id, removed.Version);
        Assert.Equal(removed, repeated);
        await using var db = database.CreateContext();
        Assert.All(await db.MediaAssets.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync(),
            x => Assert.Equal(MediaStatus.Ready, x.Status));
        Assert.Equal(2, await db.MediaAssets.CountAsync(x => x.QuestId == scenario.Seed.Quest.Id));
        Assert.Null((await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).CoverAssetId);
        Assert.Equal(7, (await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).CalendarRevision);
        var changes = await db.OutboxMessages.Where(x => x.AggregateId == scenario.Seed.Quest.Id).ToListAsync();
        Assert.Equal(3, changes.Count);
        foreach (var change in changes)
        {
            var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(change.PayloadJson)!;
            Assert.Equal(NotificationKind.QuestUpdated, envelope.Kind);
            Assert.False(envelope.CalendarChanged);
            Assert.False(envelope.MaterialChange);
        }
        Assert.Equal(2, scenario.Storage.Blobs.Count);
    }

    /// <summary>Decode failure and uncertain Blob success leave a durable pending key without publishing a replacement.</summary>
    /// <param name="providerFailure">Whether the provider stored bytes before returning a dependency failure.</param>
    /// <returns>Completion after retained old cover, pending metadata and cleanup intent assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedUpload_PreservesCoverAndDurableReclaimableIntent(bool providerFailure)
    {
        var scenario = await MediaScenario.CreateAsync(database);
        var original = await scenario.UploadAsync();
        if (providerFailure)
            scenario.Storage.AfterWrite = () => throw new DomainException(ErrorCode.DependencyUnavailable, "Synthetic unavailable provider.");
        using var input = providerFailure ? MediaScenario.Image() : new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, original.Version, input));
        Assert.Equal(providerFailure ? ErrorCode.DependencyUnavailable : ErrorCode.Validation, error.Code);
        Assert.True(input.CanRead);
        await using var db = database.CreateContext();
        var quest = await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id);
        Assert.Equal(original.AssetId, quest.CoverAssetId);
        Assert.Equal(original.Version, Convert.ToBase64String(quest.Version));
        var pending = Assert.Single(await db.MediaAssets.Where(x => x.QuestId == quest.Id && x.Status == MediaStatus.Pending).ToListAsync());
        Assert.Equal(0, pending.SizeBytes);
        Assert.Equal("", pending.ContentType);
        Assert.Equal(2, await db.ScheduledWork.CountAsync(x => x.QuestId == quest.Id && x.Type == WorkTypes.MediaCleanup));
        Assert.Equal(providerFailure ? 2 : 1, scenario.Storage.Writes);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => scenario.Service().ReadAsync(pending.Id))).Code);
    }

    /// <summary>Mutation after provider acceptance proves SQL locks are released and final attachment rechecks every editing guard.</summary>
    /// <param name="change">Permission, version or lifecycle transition introduced during private provider I/O.</param>
    /// <returns>Completion after explicit rejection, old-cover retention and reclaimable pending state.</returns>
    [Theory]
    [InlineData("version")]
    [InlineData("membership")]
    [InlineData("owner")]
    [InlineData("eligibility")]
    [InlineData("lifecycle")]
    [InlineData("expiry")]
    [InlineData("cancellation")]
    public async Task ProviderRace_ReauthorizesAndNeverPublishesStaleCover(string change)
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        var original = await scenario.UploadAsync();
        using var cancelled = new CancellationTokenSource();
        scenario.Storage.AfterWrite = async () =>
        {
            await using var edit = database.CreateContext();
            await using var transaction = await edit.BeginTransactionAsync();
            await edit.LockEventAsync(scenario.Seed.Event.Id);
            var quest = await edit.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id);
            switch (change)
            {
                case "version": quest.Title = "Concurrent edit"; break;
                case "membership":
                    (await edit.EventMemberships.SingleAsync(x => x.EventId == quest.EventId && x.UserId == scenario.Seed.User.Id)).Status = MembershipStatus.Removed;
                    break;
                case "owner":
                    edit.QuestOwners.Remove(await edit.QuestOwners.SingleAsync(x => x.QuestId == quest.Id));
                    break;
                case "eligibility":
                    (await edit.Users.SingleAsync(x => x.Id == scenario.Seed.User.Id)).IsEligible = false;
                    break;
                case "lifecycle": quest.Status = QuestStatus.Cancelled; break;
                case "expiry": scenario.Clock.Now = scenario.Clock.Now.AddHours(24); break;
                case "cancellation": await cancelled.CancelAsync(); break;
            }
            await edit.SaveChangesAsync();
            await transaction.CommitAsync();
        };
        using var input = MediaScenario.Image();
        var failure = await Record.ExceptionAsync(() => scenario.Service().UploadCoverAsync(
            scenario.Seed.Quest.Id, original.Version, input, cancelled.Token));
        if (change == "cancellation")
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else
            Assert.Contains(Assert.IsType<DomainException>(failure).Code, new[] { ErrorCode.Conflict, ErrorCode.NotFound, ErrorCode.Forbidden });
        await using var db = database.CreateContext();
        Assert.Equal(original.AssetId, (await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).CoverAssetId);
        Assert.Single(await db.MediaAssets.Where(x => x.QuestId == scenario.Seed.Quest.Id && x.Status == MediaStatus.Pending).ToListAsync());
        Assert.Equal(2, scenario.Storage.Blobs.Count);
    }

    /// <summary>Unauthorized upload does not read input, create metadata or call a provider, including administrator-only callers.</summary>
    /// <param name="grant">The absent eligibility, ownership or individual membership boundary.</param>
    /// <returns>Completion after access rejection before touching an intentionally unreadable stream.</returns>
    [Theory]
    [InlineData("moderator")]
    [InlineData("administrator")]
    [InlineData("membership")]
    [InlineData("disabled")]
    public async Task UnauthorizedUpload_DoesNotReadOrStageResources(string grant)
    {
        var scenario = await MediaScenario.CreateAsync(database, privateQuest: true);
        var user = grant is "moderator" or "administrator" ? scenario.Seed.Other : scenario.Seed.User;
        await using (var edit = database.CreateContext())
        {
            if (grant == "administrator")
                edit.Administrators.Add(new Administrator { UserId = user.Id });
            if (grant == "membership")
                (await edit.EventMemberships.SingleAsync(x => x.EventId == scenario.Seed.Event.Id && x.UserId == user.Id)).Status = MembershipStatus.Removed;
            if (grant == "disabled")
                (await edit.Users.SingleAsync(x => x.Id == user.Id)).IsEligible = false;
            await edit.SaveChangesAsync();
        }
        using var input = new RejectReadStream();
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Service(user)
            .UploadCoverAsync(scenario.Seed.Quest.Id, expectedVersion: "", content: input));
        Assert.Contains(failure.Code, new[] { ErrorCode.NotFound, ErrorCode.Forbidden });
        await using var db = database.CreateContext();
        Assert.False(await db.MediaAssets.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
        Assert.False(await db.ScheduledWork.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
        Assert.Equal(0, scenario.Storage.Writes);
    }

    /// <summary>Private moderation is explicit and audited; Draft and retained-unpublished covers are never disclosed through it.</summary>
    /// <param name="state">Published, Draft or retained cancelled-unpublished state.</param>
    /// <returns>Completion after direct-read denial and the exact audited moderation result.</returns>
    [Theory]
    [InlineData("active")]
    [InlineData("draft")]
    [InlineData("retained")]
    public async Task PrivateReads_RequireExplicitModerationAndExcludeUnpublished(string state)
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: state != "active", privateQuest: true);
        var uploaded = await scenario.UploadAsync();
        if (state == "retained")
            await scenario.Quests.ChangeStatusAsync(scenario.Seed.Quest.Id, uploaded.Version, QuestStatus.Cancelled, "");
        var moderator = scenario.Service(scenario.Seed.Other);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => moderator.ReadAsync(uploaded.AssetId!.Value))).Code);
        if (state == "active")
        {
            Assert.NotEmpty((await moderator.ReadAsync(uploaded.AssetId!.Value, true)).Data);
            await using var db = database.CreateContext();
            Assert.Equal(1, await db.AuditEntries.CountAsync(x => x.ResourceId == scenario.Seed.Quest.Id && x.Action == "ModerationCoverRead"));
        }
        else
        {
            Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() => moderator.ReadAsync(uploaded.AssetId!.Value, true))).Code);
            Assert.Equal(0, scenario.Storage.Reads);
        }
    }

    /// <summary>A successful Blob read is discarded if authorization or current assignment changes while awaiting the provider.</summary>
    /// <param name="change">Membership, invitation or assigned-cover revocation during the provider read.</param>
    /// <returns>Completion after a second server authorization denies the previously fetched bytes.</returns>
    [Theory]
    [InlineData("membership")]
    [InlineData("assignment")]
    [InlineData("invitation")]
    public async Task ReadRace_DiscardsBytesAfterRevocation(string change)
    {
        var scenario = await MediaScenario.CreateAsync(database, privateQuest: true);
        var uploaded = await scenario.UploadAsync();
        await FoundationSeed.PersistAsync(database, new QuestInvitation
        {
            QuestId = scenario.Seed.Quest.Id, UserId = scenario.Seed.Other.Id, InvitedById = scenario.Seed.User.Id,
            Status = QuestInvitationStatus.Active, ChangedUtc = scenario.Clock.Now
        });
        scenario.Storage.AfterRead = async () =>
        {
            await using var db = database.CreateContext();
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(scenario.Seed.Event.Id);
            if (change == "assignment")
                (await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).CoverAssetId = null;
            if (change == "membership")
                (await db.EventMemberships.SingleAsync(x => x.EventId == scenario.Seed.Event.Id && x.UserId == scenario.Seed.Other.Id)).Status = MembershipStatus.Removed;
            if (change == "invitation")
                (await db.QuestInvitations.SingleAsync(x => x.QuestId == scenario.Seed.Quest.Id)).Status = QuestInvitationStatus.Revoked;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        };
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Service(scenario.Seed.Other).ReadAsync(uploaded.AssetId!.Value));
        Assert.Equal(ErrorCode.NotFound, failure.Code);
        Assert.Equal(1, scenario.Storage.Reads);
    }

    /// <summary>Five admitted attempts exhaust the rolling one-minute policy; the sixth never reads input or adds pending work.</summary>
    /// <returns>Completion after rejection and exact-boundary recovery.</returns>
    [Fact]
    public async Task UploadRatePolicy_BoundsAttemptsAndReopensAtOneMinute()
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        var version = await scenario.VersionAsync();
        for (var i = 0; i < 5; i++)
        {
            using var invalid = new MemoryStream([1]);
            Assert.Equal(ErrorCode.Validation, (await Assert.ThrowsAsync<DomainException>(() =>
                scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, invalid))).Code);
        }
        using var untouched = new RejectReadStream();
        var failure = await Assert.ThrowsAsync<DomainException>(() =>
            scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, untouched));
        Assert.Equal(ErrorCode.Conflict, failure.Code);
        await using var db = database.CreateContext();
        Assert.Equal(5, await db.MediaAssets.CountAsync(x => x.QuestId == scenario.Seed.Quest.Id));
        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(1);
        Assert.NotNull((await scenario.UploadAsync()).AssetId);
    }

    /// <summary>Overdue parent completion is committed independently before an otherwise authorized cover mutation is rejected.</summary>
    /// <returns>Completion after retained lifecycle history and absence of staged upload resources.</returns>
    [Fact]
    public async Task OverdueParent_ReconcilesBeforeRejectingCoverEdit()
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        scenario.Clock.Now = new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);
        using var input = new RejectReadStream();
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Service().UploadCoverAsync(
            scenario.Seed.Quest.Id, Convert.ToBase64String(scenario.Seed.Quest.Version), input));
        Assert.Equal(ErrorCode.Conflict, failure.Code);
        await using var db = database.CreateContext();
        Assert.Equal(EventStatus.Completed, (await db.Events.SingleAsync(x => x.Id == scenario.Seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Cancelled, (await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).Status);
        Assert.Single(await db.QuestStatusHistory.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync());
        Assert.False(await db.MediaAssets.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
    }

    /// <summary>Two uploads using one captured editor version may both store, but only one can attach; the loser remains reclaimable.</summary>
    /// <returns>Completion after both providers run outside SQL, one wins, and a third upload is rejected without staging.</returns>
    [Fact]
    public async Task ConcurrentUploads_OneVersionWinnerAndBoundedNonqueuedCapacity()
    {
        var scenario = await MediaScenario.CreateAsync(database, draft: true);
        var version = await scenario.VersionAsync();
        var bothStored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stored = 0;
        scenario.Storage.AfterWrite = async () =>
        {
            if (Interlocked.Increment(ref stored) == 2)
                bothStored.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
        };
        using var firstInput = MediaScenario.Image();
        using var secondInput = MediaScenario.Image();
        var first = Record.ExceptionAsync(() => scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, firstInput));
        var second = Record.ExceptionAsync(() => scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, secondInput));
        try
        {
            await bothStored.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var thirdInput = new RejectReadStream();
            var busy = await Assert.ThrowsAsync<DomainException>(() =>
                scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, thirdInput));
            Assert.Equal(ErrorCode.DependencyUnavailable, busy.Code);
        }
        finally
        {
            release.TrySetResult();
        }
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => x is null);
        Assert.Equal(ErrorCode.Conflict, Assert.IsType<DomainException>(Assert.Single(results, x => x is not null)).Code);
        await using var db = database.CreateContext();
        var assets = await db.MediaAssets.Where(x => x.QuestId == scenario.Seed.Quest.Id).ToListAsync();
        var ready = Assert.Single(assets, x => x.Status == MediaStatus.Ready);
        Assert.Single(assets, x => x.Status == MediaStatus.Pending);
        Assert.Equal(ready.Id, (await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).CoverAssetId);
        Assert.Equal(2, scenario.Storage.Blobs.Count);
        Assert.Equal(2, await db.ScheduledWork.CountAsync(x => x.QuestId == scenario.Seed.Quest.Id));
    }

    /// <summary>Administrator roles, stale owner membership and disabled accounts do not grant image access.</summary>
    /// <param name="denial">The current persisted access boundary that is absent.</param>
    /// <returns>Completion after both relevant read paths fail before fetching private bytes.</returns>
    [Theory]
    [InlineData("administrator")]
    [InlineData("membership")]
    [InlineData("disabled")]
    public async Task Reads_DoNotBypassCurrentEligibilityOrIndividualMembership(string denial)
    {
        var scenario = await MediaScenario.CreateAsync(database, privateQuest: true);
        var result = await scenario.UploadAsync();
        var user = denial == "administrator" ? scenario.Seed.Other : scenario.Seed.User;
        await using (var edit = database.CreateContext())
        {
            if (denial == "administrator")
            {
                edit.Administrators.Add(new Administrator { UserId = user.Id });
                edit.EventOwners.Remove(await edit.EventOwners.SingleAsync(x => x.EventId == scenario.Seed.Event.Id));
            }
            if (denial is "administrator" or "membership")
                (await edit.EventMemberships.SingleAsync(x => x.EventId == scenario.Seed.Event.Id && x.UserId == user.Id)).Status = MembershipStatus.Removed;
            if (denial == "disabled")
                (await edit.Users.SingleAsync(x => x.Id == user.Id)).IsEligible = false;
            await edit.SaveChangesAsync();
        }
        foreach (var moderation in new[] { false, true })
        {
            var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Service(user).ReadAsync(result.AssetId!.Value, moderation));
            Assert.Equal(denial == "disabled" ? ErrorCode.Forbidden : ErrorCode.NotFound, failure.Code);
        }
        Assert.Equal(0, scenario.Storage.Reads);
    }

    /// <summary>Suspended cover edits notify owners and moderators without changing lifecycle or calendar revision.</summary>
    /// <returns>Completion after exact suspended-edit audience and retained status assertions.</returns>
    [Fact]
    public async Task SuspendedCoverEdit_UsesQuestAudienceWithoutRestoringCalendar()
    {
        var scenario = await MediaScenario.CreateAsync(database);
        await using (var edit = database.CreateContext())
        {
            (await edit.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).Status = QuestStatus.Suspended;
            await edit.SaveChangesAsync();
        }
        await scenario.UploadAsync();
        await using var db = database.CreateContext();
        var quest = await db.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id);
        Assert.Equal(QuestStatus.Suspended, quest.Status);
        Assert.Equal(7, quest.CalendarRevision);
        var envelope = JsonSerializer.Deserialize<ChangeEnvelope>(
            (await db.OutboxMessages.SingleAsync(x => x.AggregateId == quest.Id)).PayloadJson)!;
        Assert.Equal(NotificationKind.SuspendedQuestEdited, envelope.Kind);
        Assert.Equal(new[] { scenario.Seed.User.Id, scenario.Seed.Other.Id }.Order(),
            envelope.RecipientIds.Order());
        Assert.False(envelope.CalendarChanged);
        Assert.False(envelope.MaterialChange);
    }

    /// <summary>Closed Quest states reject new uploads before input is read or a durable pending asset is allocated.</summary>
    /// <param name="status">A read-only or terminal Quest lifecycle state.</param>
    /// <returns>Completion after explicit conflict and unchanged resource counts.</returns>
    [Theory]
    [InlineData(QuestStatus.Cancelled)]
    [InlineData(QuestStatus.Completed)]
    [InlineData(QuestStatus.Archived)]
    public async Task ClosedLifecycle_RejectsBeforeReadingOrAllocating(QuestStatus status)
    {
        var scenario = await MediaScenario.CreateAsync(database);
        await using (var edit = database.CreateContext())
        {
            (await edit.Quests.SingleAsync(x => x.Id == scenario.Seed.Quest.Id)).Status = status;
            await edit.SaveChangesAsync();
        }
        var version = await scenario.VersionAsync();
        using var stream = new RejectReadStream();
        var failure = await Assert.ThrowsAsync<DomainException>(() => scenario.Service().UploadCoverAsync(scenario.Seed.Quest.Id, version, stream));
        Assert.Equal(ErrorCode.Conflict, failure.Code);
        await using var db = database.CreateContext();
        Assert.False(await db.MediaAssets.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
        Assert.False(await db.ScheduledWork.AnyAsync(x => x.QuestId == scenario.Seed.Quest.Id));
    }

    private sealed class RejectReadStream : MemoryStream
    {
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Authorization must precede reading input.");
    }
}
