using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Events.Implementation;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreEvents;

/// <summary>Verifies the shared Event reconciler stages complete, repeat-safe effects without saving or owning the caller transaction.</summary>
/// <param name="database">Uniquely owned migrated SQL fixture; each case uses independent aggregate identifiers.</param>
public sealed class EventLifecycleReconcilerTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Stages parent, pending consent, and child effects once; only the caller's save assigns versions and persists history.</summary>
    /// <returns>A task completing after tracked-state, caller-owned persistence, and post-commit idempotency assertions.</returns>
    [Fact]
    public async Task ReconcileStagesAllEffectsWithoutSavingAndCallerCommitsOnce()
    {
        var context = new EventTestContext(database);
        context.Quests.FlushChildSql = false;
        var seed = await context.SeedAsync();
        var end = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End.ToUniversalTime();
        var request = new EventMembershipRequest { EventId = seed.Event.Id, UserId = seed.Other.Id, CreatedUtc = FoundationSeed.Now };
        var invitation = new EventInvitation
        {
            EventId = seed.Event.Id, UserId = seed.Other.Id, InvitedById = seed.User.Id,
            CreatedUtc = FoundationSeed.Now, ExpiresUtc = end
        };
        await FoundationSeed.PersistAsync(database, request, invitation);
        var reconciler = new EventLifecycleReconciler(context.Quests);
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(seed.Event.Id);
            Assert.True(await reconciler.ReconcileAsync(db, seed.Event.Id, end));
            var parent = Assert.Single(db.Events.Local);
            var child = Assert.Single(db.Quests.Local);
            Assert.Equal(EventStatus.Completed, parent.Status);
            Assert.Equal(QuestStatus.Completed, child.Status);
            Assert.Equal(EntityState.Modified, db.Entry(parent).State);
            Assert.Equal(EntityState.Modified, db.Entry(child).State);
            Assert.Equal(seed.Event.Version, parent.Version);
            var history = Assert.Single(db.EventStatusHistory.Local);
            Assert.Equal(EventStatus.Active, history.Previous);
            Assert.Equal(EventStatus.Completed, history.Next);
            Assert.Null(history.ActorId);
            Assert.Equal(end, history.OccurredUtc);
            Assert.Empty(history.Version);
            Assert.Equal(EntityState.Added, db.Entry(history).State);
            Assert.Empty(Assert.Single(db.AuditEntries.Local).Version);
            Assert.Equal(MembershipRequestStatus.Rejected, Assert.Single(db.MembershipRequests.Local).Status);
            Assert.Equal(EventInvitationStatus.Expired, Assert.Single(db.EventInvitations.Local).Status);
            Assert.Empty(db.OutboxMessages.Local);
            Assert.False(await reconciler.ReconcileAsync(db, seed.Event.Id, end));
            Assert.Single(context.Quests.Calls);
            await db.SaveChangesAsync();
            Assert.NotEmpty(history.Version);
            await transaction.CommitAsync();
        }
        await using var read = database.CreateContext();
        await using var nextTransaction = await read.BeginTransactionAsync();
        await read.LockEventAsync(seed.Event.Id);
        Assert.False(await reconciler.ReconcileAsync(read, seed.Event.Id, end.AddHours(1)));
        Assert.Equal(EventStatus.Completed, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Completed, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
        Assert.Equal(MembershipRequestStatus.Rejected, (await read.MembershipRequests.SingleAsync(x => x.Id == request.Id)).Status);
        Assert.Equal(EventInvitationStatus.Expired, (await read.EventInvitations.SingleAsync(x => x.Id == invitation.Id)).Status);
        Assert.Single(await read.EventStatusHistory.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.Single(context.Quests.Calls);
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
    }

    /// <summary>Only an Active Event at or beyond its exact inclusive-end boundary stages completion.</summary>
    /// <param name="status">Persisted lifecycle state before reconciliation.</param>
    /// <param name="offsetTicks">Ticks before or after the exclusive UTC end boundary.</param>
    /// <param name="changed">Whether the caller must persist staged completion.</param>
    /// <returns>A task completing after exact state, callback, and pending-change assertions.</returns>
    [Theory]
    [InlineData(EventStatus.Active, -1, false)]
    [InlineData(EventStatus.Active, 0, true)]
    [InlineData(EventStatus.Active, 1, true)]
    [InlineData(EventStatus.Draft, 1, false)]
    [InlineData(EventStatus.Completed, 1, false)]
    [InlineData(EventStatus.Cancelled, 1, false)]
    [InlineData(EventStatus.Archived, 1, false)]
    public async Task ReconcileUsesExactBoundaryAndAllowedSourceState(EventStatus status, long offsetTicks, bool changed)
    {
        var context = new EventTestContext(database);
        context.Quests.FlushChildSql = false;
        var seed = await context.SeedAsync();
        await using (var setup = database.CreateContext())
        {
            (await setup.Events.FindAsync(seed.Event.Id))!.Status = status;
            await setup.SaveChangesAsync();
        }
        var now = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End.AddTicks(offsetTicks);
        await using var db = database.CreateContext();
        await using var transaction = await db.BeginTransactionAsync();
        await db.LockEventAsync(seed.Event.Id);
        Assert.Equal(changed, await new EventLifecycleReconciler(context.Quests).ReconcileAsync(db, seed.Event.Id, now));
        Assert.Equal(changed ? EventStatus.Completed : status, Assert.Single(db.Events.Local).Status);
        Assert.Equal(changed, db.ChangeTracker.HasChanges());
        Assert.Equal(changed ? 1 : 0, context.Quests.Calls.Count);
        Assert.Equal(changed ? 1 : 0, db.EventStatusHistory.Local.Count);
    }

    /// <summary>A staged child failure propagates without saving; disposing the caller transaction leaves both aggregates unchanged.</summary>
    /// <returns>A task completing after the failed caller scope and independent SQL reread.</returns>
    [Fact]
    public async Task ChildFailureDoesNotSaveOrCommitPartialReconciliation()
    {
        var context = new EventTestContext(database);
        context.Quests.FlushChildSql = false;
        context.Quests.FailureAfterChildSave = new DomainException(ErrorCode.DependencyUnavailable, "Controlled child failure.");
        var seed = await context.SeedAsync();
        var now = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End;
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(seed.Event.Id);
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                new EventLifecycleReconciler(context.Quests).ReconcileAsync(db, seed.Event.Id, now));
            Assert.Equal(ErrorCode.DependencyUnavailable, error.Code);
            Assert.Equal(QuestStatus.Completed, Assert.Single(db.Quests.Local).Status);
            Assert.Equal(EntityState.Modified, db.Entry(Assert.Single(db.Quests.Local)).State);
            Assert.Empty(db.EventStatusHistory.Local);
        }
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Active, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Equal(QuestStatus.Active, (await read.Quests.SingleAsync(x => x.Id == seed.Quest.Id)).Status);
        Assert.Empty(await read.EventStatusHistory.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.Empty(await read.OutboxMessages.Where(x => x.AggregateId == seed.Event.Id).ToListAsync());
    }

    /// <summary>Previously committed system reconciliation survives a subsequently rejected user command, without committing that user mutation.</summary>
    /// <returns>A task completing after separate system and user transactions are verified.</returns>
    [Fact]
    public async Task DedicatedReconciliationSurvivesRejectedMembershipCommand()
    {
        var context = new EventTestContext(database);
        context.Quests.FlushChildSql = false;
        var seed = await context.SeedAsync();
        context.Clock.Now = TimeRules.EventWindow(seed.Event.StartDate, seed.Event.EndDate, seed.Event.TimeZoneId).End;
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(seed.Event.Id);
            Assert.True(await new EventLifecycleReconciler(context.Quests).ReconcileAsync(db, seed.Event.Id, context.Clock.Now));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        Assert.Equal(ErrorCode.Conflict, (await Assert.ThrowsAsync<DomainException>(() =>
            context.Service(seed.Other).RequestMembershipAsync(seed.Event.Id))).Code);
        await using var read = database.CreateContext();
        Assert.Equal(EventStatus.Completed, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Status);
        Assert.Single(await read.EventStatusHistory.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id && x.UserId == seed.Other.Id));
        Assert.Empty(await read.MembershipRequests.Where(x => x.EventId == seed.Event.Id).ToListAsync());
        Assert.Single(context.Quests.Calls);
    }

    /// <summary>A missing Event fails safely after the caller locks its absent key range; no resource is created implicitly.</summary>
    /// <returns>A task completing after unavailable-resource and no-write assertions.</returns>
    [Fact]
    public async Task MissingEventIsUnavailableWithoutImplicitCreation()
    {
        var context = new EventTestContext(database);
        var id = Guid.NewGuid();
        await using var db = database.CreateContext();
        await using var transaction = await db.BeginTransactionAsync();
        await db.LockEventAsync(id);
        Assert.Equal(ErrorCode.NotFound, (await Assert.ThrowsAsync<DomainException>(() =>
            new EventLifecycleReconciler(context.Quests).ReconcileAsync(db, id, FoundationSeed.Now))).Code);
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Empty(context.Quests.Calls);
    }
}
