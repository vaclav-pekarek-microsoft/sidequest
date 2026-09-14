using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Tests durable complete snapshots, replay and per-recipient authorization without remote group access.</summary>
/// <param name="database">Uniquely owned migrated SQL database used for real transactional effects.</param>
public sealed class BulkMembershipTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Waits for the complete directory result, deduplicates it and freezes recipients before granting individual membership.</summary>
    /// <returns>A task completing after controlled expansion and idempotent handler replay.</returns>
    [Fact]
    public async Task CompleteExpansionPrecedesMembershipAndDeduplicatesSnapshot()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expansion = new TaskCompletionSource<IReadOnlyList<DirectoryUser>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Directory.Expand = _ => { entered.SetResult(); return expansion.Task; };
        var operationId = await context.Service(seed.User).StartBulkAsync(seed.Event.Id, Guid.NewGuid(), BulkMode.Add);
        Guid workId;
        await using (var read = database.CreateContext())
            workId = (await read.ScheduledWork.SingleAsync(x => x.PayloadJson.Contains(operationId.ToString()))).Id;
        var execution = context.Bulk().ExecuteAsync(workId, default);
        await entered.Task;
        try
        {
            await using var duringExpansion = database.CreateContext();
            Assert.False(await duringExpansion.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
            Assert.Empty(await duringExpansion.BulkRecipients.Where(x => x.OperationId == operationId).ToListAsync());
            Assert.Null((await duringExpansion.BulkOperations.SingleAsync(x => x.Id == operationId)).SnapshotUtc);
        }
        finally
        {
            var person = context.Directory.Users[seed.Other.ObjectId];
            expansion.TrySetResult([person, person]);
        }
        await execution;
        await context.Bulk().ExecuteAsync(workId, default);
        await using var readFinal = database.CreateContext();
        var recipient = await readFinal.BulkRecipients.SingleAsync(x => x.OperationId == operationId);
        Assert.Equal(seed.Other.Id, recipient.UserId);
        Assert.Equal(BulkRecipientStatus.Applied, recipient.Status);
        var operation = await readFinal.BulkOperations.SingleAsync(x => x.Id == operationId);
        Assert.Equal(FoundationSeed.Now, operation.SnapshotUtc);
        Assert.Equal(BulkStatus.Completed, operation.Status);
        Assert.Null(operation.LastError);
        Assert.Equal(MembershipStatus.Active, (await readFinal.EventMemberships.SingleAsync(x =>
            x.EventId == seed.Event.Id && x.UserId == seed.Other.Id)).Status);
        Assert.Single(await readFinal.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Bulk.SnapshotFrozen").ToListAsync());
        Assert.Single(await readFinal.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Bulk.Outcome").ToListAsync());
        Assert.Equal(1, context.Directory.ExpansionCalls);
        Assert.Equal(0, context.Directory.UserCalls);
        var messages = await readFinal.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync();
        var activation = Assert.Single(messages.Select(x => JsonSerializer.Deserialize<ChangeEnvelope>(x.PayloadJson)!),
            x => x.Kind == NotificationKind.MembershipAdded);
        Assert.Equal(new[] { seed.Other.Id }, activation.AffectedUserIds);
        Assert.Equal(seed.User.Id, activation.ActorId);
        Assert.Equal(new[] { seed.User.Id, seed.Other.Id }.Order(), activation.RecipientIds.Order());
    }

    /// <summary>Reports failed expansion truthfully and never persists a partial recipient snapshot or membership.</summary>
    /// <param name="conflicting">Whether failure is inconsistent duplicate directory records rather than a provider exception.</param>
    /// <returns>A task completing after repeated failure and unchanged audience assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpansionFailureCannotApplyPartialMembership(bool conflicting)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var person = context.Directory.Users[seed.Other.ObjectId];
        context.Directory.Expand = _ => conflicting
            ? Task.FromResult<IReadOnlyList<DirectoryUser>>([person, person with { DisplayName = "Conflicting" }])
            : Task.FromException<IReadOnlyList<DirectoryUser>>(new DomainException(ErrorCode.DependencyUnavailable, "Incomplete controlled expansion"));
        var operation = Operation(seed);
        var work = Work(operation.Id);
        await FoundationSeed.PersistAsync(database, operation, work);
        for (var repeat = 0; repeat < 2; repeat++)
            Assert.Equal(ErrorCode.DependencyUnavailable, (await Assert.ThrowsAsync<DomainException>(() => context.Bulk().ExecuteAsync(work.Id, default))).Code);
        await using var read = database.CreateContext();
        var saved = await read.BulkOperations.SingleAsync(x => x.Id == operation.Id);
        Assert.Equal(BulkStatus.Failed, saved.Status);
        Assert.Null(saved.SnapshotUtc);
        Assert.Contains("no membership changes", saved.LastError);
        Assert.Empty(await read.BulkRecipients.Where(x => x.OperationId == operation.Id).ToListAsync());
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Bulk.Outcome").ToListAsync());
    }

    /// <summary>Resumes a saved snapshot without querying changed directory data and reports partial recipient failure while committing valid individuals.</summary>
    /// <returns>A task completing after replay, independent recipient outcomes and delivery deduplication.</returns>
    [Fact]
    public async Task FrozenSnapshotReplayDoesNotRefetchAndRetainsPartialFailures()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var revoked = FoundationSeed.NewUser();
        revoked.TenantId = seed.User.TenantId;
        revoked.IsEligible = false;
        await FoundationSeed.PersistAsync(database, revoked);
        var operation = Operation(seed, frozen: true);
        var work = Work(operation.Id);
        await FoundationSeed.PersistAsync(database, operation, work,
            new BulkMembershipRecipient { OperationId = operation.Id, UserId = seed.Other.Id },
            new BulkMembershipRecipient { OperationId = operation.Id, UserId = revoked.Id });
        context.Directory.Expand = _ => throw new InvalidOperationException("Frozen snapshots must not refetch");
        await context.Bulk().ExecuteAsync(work.Id, default);
        await context.Bulk().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        var recipients = await read.BulkRecipients.Where(x => x.OperationId == operation.Id).ToListAsync();
        Assert.Equal(2, recipients.Count);
        Assert.Equal(BulkRecipientStatus.Applied, recipients.Single(x => x.UserId == seed.Other.Id).Status);
        Assert.Equal(BulkRecipientStatus.Failed, recipients.Single(x => x.UserId == revoked.Id).Status);
        Assert.Contains("Validation", recipients.Single(x => x.UserId == revoked.Id).Detail);
        Assert.True(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == revoked.Id));
        var saved = await read.BulkOperations.SingleAsync(x => x.Id == operation.Id);
        Assert.Equal(BulkStatus.Completed, saved.Status);
        Assert.Contains("1 individuals", saved.LastError);
        Assert.Equal(0, context.Directory.ExpansionCalls);
        Assert.Equal(0, context.Directory.UserCalls);
        Assert.Single(await read.AuditEntries.Where(x => x.ResourceId == seed.Event.Id && x.Action == "Bulk.Outcome").ToListAsync());
        var summary = await context.Service(seed.User).GetBulkAsync(operation.Id);
        Assert.Equal((2, 1, 0, 1), (summary.Total, summary.Applied, summary.Skipped, summary.Failed));
        Assert.Equal(BulkStatus.Completed, summary.Status);
        Assert.Contains("1 individuals", summary.Error);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            context.Service(seed.Other).GetBulkAsync(operation.Id))).Code);
    }

    /// <summary>Preserves explicit removals and newer invitation revocations instead of allowing stale bulk work to resurrect access.</summary>
    /// <param name="mode">Saved bulk command mode.</param>
    /// <param name="revocation">Whether consent revocation rather than removed membership wins.</param>
    /// <returns>A task completing after skipped recipient and unchanged access checks.</returns>
    [Theory]
    [InlineData(BulkMode.Add, false)]
    [InlineData(BulkMode.Invite, false)]
    [InlineData(BulkMode.Add, true)]
    [InlineData(BulkMode.Invite, true)]
    public async Task RemovalOrRevocationWinsAgainstStaleSnapshot(BulkMode mode, bool revocation)
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var operation = Operation(seed, frozen: true);
        operation.Mode = mode;
        var work = Work(operation.Id);
        await FoundationSeed.PersistAsync(database, operation, work,
            new BulkMembershipRecipient { OperationId = operation.Id, UserId = seed.Other.Id });
        if (revocation)
            await FoundationSeed.PersistAsync(database, new EventInvitation
            {
                EventId = seed.Event.Id, UserId = seed.Other.Id, InvitedById = seed.User.Id,
                Status = EventInvitationStatus.Revoked, CreatedUtc = FoundationSeed.Now.AddHours(-1),
                ExpiresUtc = FoundationSeed.Now.AddDays(1), ResolvedUtc = operation.CreatedUtc
            });
        else
            await FoundationSeed.PersistAsync(database, seed.Membership(seed.Other.Id, MembershipStatus.Removed));
        await context.Bulk().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        var recipient = await read.BulkRecipients.SingleAsync(x => x.OperationId == operation.Id);
        Assert.Equal(BulkRecipientStatus.Skipped, recipient.Status);
        Assert.Contains("takes precedence", recipient.Detail);
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id &&
            x.UserId == seed.Other.Id && x.Status == MembershipStatus.Active));
        Assert.False(await read.EventInvitations.AnyAsync(x => x.EventId == seed.Event.Id &&
            x.UserId == seed.Other.Id && x.Status == EventInvitationStatus.Pending));
        Assert.Equal(0, context.Directory.ExpansionCalls);
    }

    /// <summary>Reauthorizes an initiator whose eligibility is revoked after the snapshot, failing recipients without granting access.</summary>
    /// <returns>A task completing after workflow resumption and persisted failure checks.</returns>
    [Fact]
    public async Task ActorDeauthorizedAfterSnapshotCannotApplyRecipients()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var operation = Operation(seed, frozen: true);
        var work = Work(operation.Id);
        await FoundationSeed.PersistAsync(database, operation, work,
            new BulkMembershipRecipient { OperationId = operation.Id, UserId = seed.Other.Id });
        await using (var revoke = database.CreateContext())
        {
            (await revoke.Users.FindAsync(seed.User.Id))!.IsEligible = false;
            await revoke.SaveChangesAsync();
        }
        await context.Bulk().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        var recipient = await read.BulkRecipients.SingleAsync(x => x.OperationId == operation.Id);
        Assert.Equal(BulkRecipientStatus.Failed, recipient.Status);
        Assert.Contains("Forbidden", recipient.Detail);
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Contains("1 individuals", (await read.BulkOperations.SingleAsync(x => x.Id == operation.Id)).LastError);
        Assert.Equal(0, context.Directory.ExpansionCalls);
    }

    /// <summary>Rejects unsupported persisted bulk schemas before expanding a group or changing a saved operation.</summary>
    /// <returns>A task completing after payload rejection and persisted no-effect checks.</returns>
    [Fact]
    public async Task UnknownBulkPayloadSchemaFailsBeforeDirectory()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var operation = Operation(seed);
        var work = Work(operation.Id);
        work.PayloadJson = JsonSerializer.Serialize(new BulkMembershipPayload(2, operation.Id));
        await FoundationSeed.PersistAsync(database, operation, work);
        var error = await Assert.ThrowsAsync<DomainException>(() => context.Bulk().ExecuteAsync(work.Id, default));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(0, context.Directory.ExpansionCalls);
        await using var read = database.CreateContext();
        Assert.Equal(BulkStatus.Expanding, (await read.BulkOperations.SingleAsync(x => x.Id == operation.Id)).Status);
        Assert.Empty(await read.BulkRecipients.Where(x => x.OperationId == operation.Id).ToListAsync());
    }

    /// <summary>Applies the invite mode as named consent, not direct membership, and preserves one invitation across replay.</summary>
    /// <returns>A task completing after persisted invite-mode outcomes.</returns>
    [Fact]
    public async Task FrozenInviteSnapshotCreatesConsentWithoutMembership()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        var operation = Operation(seed, frozen: true);
        operation.Mode = BulkMode.Invite;
        var work = Work(operation.Id);
        await FoundationSeed.PersistAsync(database, operation, work,
            new BulkMembershipRecipient { OperationId = operation.Id, UserId = seed.Other.Id });
        await context.Bulk().ExecuteAsync(work.Id, default);
        await context.Bulk().ExecuteAsync(work.Id, default);
        await using var read = database.CreateContext();
        var invitation = await read.EventInvitations.SingleAsync(x => x.EventId == seed.Event.Id);
        Assert.Equal(seed.Other.Id, invitation.UserId);
        Assert.Equal(seed.User.Id, invitation.InvitedById);
        Assert.Equal(EventInvitationStatus.Pending, invitation.Status);
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Equal(BulkRecipientStatus.Applied, (await read.BulkRecipients.SingleAsync(x => x.OperationId == operation.Id)).Status);
        Assert.Equal(0, context.Directory.ExpansionCalls);
    }

    /// <summary>Rechecks the initiator after remote expansion, rolling back snapshot creation when ownership was removed while the provider worked.</summary>
    /// <returns>A task completing after controlled mid-expansion revocation and no-effect checks.</returns>
    [Fact]
    public async Task ActorLosingOwnershipDuringExpansionCannotFreezeSnapshot()
    {
        var context = new EventTestContext(database);
        var seed = await context.SeedAsync();
        context.Directory.Expand = async _ =>
        {
            await using var revoke = database.CreateContext();
            revoke.EventOwners.Remove(await revoke.EventOwners.SingleAsync(x => x.EventId == seed.Event.Id));
            await revoke.SaveChangesAsync();
            return context.Directory.Users.Values.ToArray();
        };
        var operation = Operation(seed);
        var work = Work(operation.Id);
        await FoundationSeed.PersistAsync(database, operation, work);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            context.Bulk().ExecuteAsync(work.Id, default))).Code);
        await using var read = database.CreateContext();
        var saved = await read.BulkOperations.SingleAsync(x => x.Id == operation.Id);
        Assert.Equal(BulkStatus.Failed, saved.Status);
        Assert.Null(saved.SnapshotUtc);
        Assert.Empty(await read.BulkRecipients.Where(x => x.OperationId == operation.Id).ToListAsync());
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Equal(1, context.Directory.ExpansionCalls);
    }

    private static BulkMembershipOperation Operation(FoundationSeed seed, bool frozen = false) => new()
    {
        EventId = seed.Event.Id, ActorId = seed.User.Id, SourceGroupId = Guid.NewGuid(), Mode = BulkMode.Add,
        CreatedUtc = FoundationSeed.Now, SnapshotUtc = frozen ? FoundationSeed.Now : null,
        Status = frozen ? BulkStatus.Applying : BulkStatus.Expanding
    };

    private static ScheduledWork Work(Guid operationId) => new()
    {
        Type = WorkTypes.BulkMembership, DeduplicationKey = $"test-bulk:{operationId:N}",
        DueUtc = FoundationSeed.Now, PayloadJson = JsonSerializer.Serialize(new BulkMembershipPayload(1, operationId))
    };
}
