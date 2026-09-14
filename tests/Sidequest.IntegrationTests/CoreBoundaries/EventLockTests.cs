using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Security;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreBoundaries;

/// <summary>Checks explicit transaction preconditions, lock lifetime, serialization, cancellation, and absence of authorization side effects.</summary>
/// <param name="database">The existing fixture owning this class's migrated SQL catalog.</param>
public sealed class EventLockTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Checks missing and non-Serializable transactions without allowing the lock operation to complete their lifecycle.</summary>
    /// <param name="isolation">Null for no explicit transaction, otherwise a supported but invalid lock isolation level.</param>
    /// <returns>A task completing after exact error and unchanged tracker/transaction assertions.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task LockEventAsync_InvalidTransaction_RejectsWithoutTracking(IsolationLevel? isolation)
    {
        await using var db = database.CreateContext();
        await using var transaction = isolation is null ? null : await db.BeginTransactionAsync(isolation.Value);
        ISidequestDbContext boundary = db;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.LockEventAsync(Guid.NewGuid()));
        Assert.Equal("An explicit Serializable transaction is required before locking an Event.", error.Message);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
        if (transaction is not null)
            await transaction.CommitAsync();
    }

    /// <summary>Checks that empty identifiers fail validation before transaction prerequisites.</summary>
    /// <param name="transactional">Whether an otherwise valid Serializable transaction is supplied.</param>
    /// <returns>A task completing after exact field validation and no-tracking assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockEventAsync_EmptyIdentifier_Rejects(bool transactional)
    {
        await using var db = database.CreateContext();
        await using var transaction = transactional ? await db.BeginTransactionAsync() : null;
        var error = await Assert.ThrowsAsync<DomainException>(() => db.LockEventAsync(Guid.Empty));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("An Event identifier is required.", error.Message);
        Assert.Equal("eventId", error.Field);
        Assert.Null(error.InnerException);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Checks cancellation before identifier and transaction validation.</summary>
    /// <param name="empty">Whether cancellation must take precedence over an empty identifier.</param>
    /// <param name="transactional">Whether a valid transaction already exists.</param>
    /// <returns>A task completing after exact cancellation and unchanged tracking/transaction assertions.</returns>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task LockEventAsync_PreCancelled_PrecedesValidation(bool empty, bool transactional)
    {
        await using var db = database.CreateContext();
        await using var transaction = transactional ? await db.BeginTransactionAsync() : null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => db.LockEventAsync(empty ? Guid.Empty : Guid.NewGuid(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Same(transaction, db.Database.CurrentTransaction);
    }

    /// <summary>Checks successful existing/missing-key locks neither track, save, complete the transaction, nor grant resource access.</summary>
    /// <param name="existing">Whether the requested Event key exists.</param>
    /// <param name="staged">Whether an unrelated account insertion is pending during the lock.</param>
    /// <returns>A task completing after tracker snapshots, safe denial, and independent durable-state assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LockEventAsync_DoesNotTrackSaveCompleteOrGrantAccess(bool existing, bool staged)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var key = existing ? seed.Event.Id : Guid.NewGuid();
        var pending = FoundationSeed.NewUser();
        await using var db = database.CreateContext();
        if (staged)
            db.Users.Add(pending);
        await using var transaction = await db.BeginTransactionAsync();
        var before = db.ChangeTracker.Entries().Select(x => (x.Entity, x.State)).ToArray();
        await db.LockEventAsync(key);
        Assert.Equal(before, db.ChangeTracker.Entries().Select(x => (x.Entity, x.State)).ToArray());
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(IsolationLevel.Serializable, transaction.GetDbTransaction().IsolationLevel);
        var access = new ResourceAccess(StubCurrentUser.For(seed.User));
        var error = await Assert.ThrowsAsync<DomainException>(
            () => access.RequireEventAsync(db, key, seed.User.Id));
        Assert.Equal(ErrorCode.NotFound, error.Code);
        Assert.Equal("This resource is unavailable.", error.Message);
        await transaction.CommitAsync();
        await using var read = database.CreateContext();
        Assert.False(await read.Users.AnyAsync(x => x.Id == pending.Id));
        Assert.Equal(existing, await read.Events.AnyAsync(x => x.Id == key));
        Assert.Equal(seed.Event.Version, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Version);
        Assert.False(await read.EventMemberships.AnyAsync(x => x.EventId == seed.Event.Id));
        Assert.False(await read.EventOwners.AnyAsync(x => x.EventId == seed.Event.Id));
        Assert.Empty(pending.Version);
    }

    /// <summary>Proves a second same-key mutator waits for caller completion and then sees only committed state, including absent-key ranges.</summary>
    /// <param name="existing">Whether the key names an existing Event or a missing insertion key.</param>
    /// <param name="commit">Whether the first caller commits its staged mutation or rolls it back.</param>
    /// <returns>A task completing after observed SQL blocking, both callers, and exact final content assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LockEventAsync_SameKeySerializesMutators_UntilCommitOrRollback(bool existing, bool commit)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var key = existing ? seed.Event.Id : Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await first.Database.OpenConnectionAsync(deadline.Token);
        await second.Database.OpenConnectionAsync(deadline.Token);
        var firstId = await SqlBoundaryCoordinator.SessionAsync((SqlConnection)first.Database.GetDbConnection());
        var secondId = await SqlBoundaryCoordinator.SessionAsync((SqlConnection)second.Database.GetDbConnection());
        await using var transaction = await first.BeginTransactionAsync();
        await first.LockEventAsync(key, deadline.Token);
        Task<string> NextAsync() => MutateSecondAsync();
        var contender = NextAsync();
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, secondId, firstId, deadline.Token);
            Assert.False(contender.IsCompleted);
            var row = existing ? await first.Events.SingleAsync(x => x.Id == key) : FoundationSeed.NewEvent(seed.Other.Id);
            row.Id = key;
            row.Name = "First committed";
            if (!existing)
                first.Events.Add(row);
            Assert.Equal(1, await first.SaveChangesAsync(deadline.Token));
            if (commit)
                await transaction.CommitAsync(deadline.Token);
            else
                await transaction.RollbackAsync(deadline.Token);
            Assert.Equal(commit ? "First committed" : existing ? "Sensitive Event sentinel" : "Absent", await contender);
            await using var read = database.CreateContext();
            Assert.Equal("Second committed", (await read.Events.SingleAsync(x => x.Id == key)).Name);
            Assert.Equal(1, await read.Events.CountAsync(x => x.Id == key));
        }
        finally
        {
            deadline.Cancel();
            await transaction.DisposeAsync();
            await Record.ExceptionAsync(async () => await contender);
        }

        async Task<string> MutateSecondAsync()
        {
            await using var nextTransaction = await second.BeginTransactionAsync(cancellationToken: deadline.Token);
            await second.LockEventAsync(key, deadline.Token);
            var row = await second.Events.SingleOrDefaultAsync(x => x.Id == key, deadline.Token);
            var observed = row?.Name ?? "Absent";
            if (row is null)
            {
                row = FoundationSeed.NewEvent(seed.Other.Id);
                row.Id = key;
                second.Events.Add(row);
            }
            row.Name = "Second committed";
            Assert.Equal(1, await second.SaveChangesAsync(deadline.Token));
            await nextTransaction.CommitAsync(deadline.Token);
            return observed;
        }
    }

    /// <summary>Proves an absent-key lock blocks an ordinary competing INSERT even when the inserter does not request the lock abstraction.</summary>
    /// <param name="commit">Whether the missing-key holder releases its transaction with commit or rollback.</param>
    /// <returns>A task completing after observed insert blocking and exact durable inserted-row assertions after release.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LockEventAsync_AbsentKeyRange_BlocksDirectInsertUntilCompletion(bool commit)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var pending = FoundationSeed.NewEvent(seed.Other.Id);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var holder = database.CreateContext();
        await using var inserter = database.CreateContext();
        await holder.Database.OpenConnectionAsync(deadline.Token);
        await inserter.Database.OpenConnectionAsync(deadline.Token);
        var holderId = await SqlBoundaryCoordinator.SessionAsync((SqlConnection)holder.Database.GetDbConnection());
        var inserterId = await SqlBoundaryCoordinator.SessionAsync((SqlConnection)inserter.Database.GetDbConnection());
        await using var held = await holder.BeginTransactionAsync();
        await holder.LockEventAsync(pending.Id, deadline.Token);
        inserter.Events.Add(pending);
        var inserting = inserter.SaveChangesAsync(deadline.Token);
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, inserterId, holderId, deadline.Token);
            Assert.False(inserting.IsCompleted);
            Assert.Empty(holder.ChangeTracker.Entries());
            if (commit)
                await held.CommitAsync(deadline.Token);
            else
                await held.RollbackAsync(deadline.Token);
            Assert.Equal(1, await inserting);
            Assert.Equal(EntityState.Unchanged, inserter.Entry(pending).State);
            await using var read = database.CreateContext();
            var stored = await read.Events.SingleAsync(x => x.Id == pending.Id);
            Assert.Equal("Sensitive Event sentinel", stored.Name);
            Assert.Equal(seed.Other.Id, stored.CreatorId);
            Assert.Equal(8, stored.Version.Length);
        }
        finally
        {
            deadline.Cancel();
            await held.DisposeAsync();
            await Record.ExceptionAsync(async () => await inserting);
        }
    }

    /// <summary>Proves an existing different Event remains independently lockable while a same-key waiter is cancelled.</summary>
    /// <returns>A task completing after observed contention, cancellation, independent mutation and fresh acquisition after release.</returns>
    [Fact]
    public async Task LockEventAsync_BlockedCancellationAndDifferentEvent_PreserveIndependentProgress()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var other = FoundationSeed.NewEvent(seed.Other.Id);
        await FoundationSeed.PersistAsync(database, other);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var holder = database.CreateContext();
        await using var waiter = database.CreateContext();
        await holder.Database.OpenConnectionAsync(deadline.Token);
        await waiter.Database.OpenConnectionAsync(deadline.Token);
        var holderId = await SqlBoundaryCoordinator.SessionAsync((SqlConnection)holder.Database.GetDbConnection());
        var waiterId = await SqlBoundaryCoordinator.SessionAsync((SqlConnection)waiter.Database.GetDbConnection());
        await using var held = await holder.BeginTransactionAsync();
        await using var waiting = await waiter.BeginTransactionAsync();
        await holder.LockEventAsync(seed.Event.Id, deadline.Token);
        var blocked = waiter.LockEventAsync(seed.Event.Id, waiterCancellation.Token);
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, waiterId, holderId, deadline.Token);
            await using (var independent = database.CreateContext())
            {
                await using var different = await independent.BeginTransactionAsync();
                await independent.LockEventAsync(other.Id, deadline.Token);
                (await independent.Events.SingleAsync(x => x.Id == other.Id, deadline.Token)).Name = "Independent";
                Assert.Equal(1, await independent.SaveChangesAsync(deadline.Token));
                await different.CommitAsync(deadline.Token);
            }
            Assert.False(blocked.IsCompleted);
            waiterCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            await waiting.RollbackAsync(deadline.Token);
            Assert.Empty(waiter.ChangeTracker.Entries());
            await held.CommitAsync(deadline.Token);
            await using var fresh = database.CreateContext();
            await using var acquired = await fresh.BeginTransactionAsync();
            await fresh.LockEventAsync(seed.Event.Id, deadline.Token);
            Assert.Equal("Independent", (await fresh.Events.SingleAsync(x => x.Id == other.Id, deadline.Token)).Name);
            Assert.Equal(seed.Event.Version, (await fresh.Events.SingleAsync(x => x.Id == seed.Event.Id, deadline.Token)).Version);
        }
        finally
        {
            waiterCancellation.Cancel();
            await held.DisposeAsync();
            await Record.ExceptionAsync(() => blocked);
        }
    }
}
