using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreBoundaries;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks the participation reservation's caller-owned transaction, exact tracked state, validation and cancellation boundaries.</summary>
/// <param name="database">The uniquely owned migrated SQL catalog for these boundary scenarios.</param>
public sealed class ParticipationReservationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Missing and weaker transactions cannot reserve a participation until the caller's mutation completes.</summary>
    /// <param name="isolation">Null for no transaction, or a weaker isolation level to reject.</param>
    /// <returns>Completion after the exact error, unchanged transaction ownership and no tracked rows.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task InvalidTransaction_IsRejected(IsolationLevel? isolation)
    {
        await using var db = database.CreateContext();
        await using var transaction = isolation is null ? null : await db.BeginTransactionAsync(isolation.Value);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.FindQuestParticipationForUpdateAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("An explicit Serializable transaction is required before reserving a Quest participation.", error.Message);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Empty identifiers fail before transaction checks or database queries.</summary>
    /// <param name="emptyQuest">Whether the Quest identifier, rather than the actor identifier, is absent.</param>
    /// <returns>Completion after exact validation code/field assertions and no tracked rows.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyIdentifier_IsRejected(bool emptyQuest)
    {
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            db.FindQuestParticipationForUpdateAsync(emptyQuest ? Guid.Empty : Guid.NewGuid(), emptyQuest ? Guid.NewGuid() : Guid.Empty));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(emptyQuest ? "questId" : "userId", error.Field);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Pre-cancellation takes precedence over invalid identifiers and a missing transaction without querying SQL.</summary>
    /// <returns>Completion after the original token, absent transaction and empty tracker are asserted.</returns>
    [Fact]
    public async Task PreCancelled_DoesNotQuery()
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            db.FindQuestParticipationForUpdateAsync(Guid.Empty, Guid.Empty, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Every retained participation state is returned by exact pair with tracking and current rowversion, without saving or completing the transaction.</summary>
    /// <param name="status">The retained exclusive state to read unchanged.</param>
    /// <returns>Completion after neighbouring-key absence, exact state/version, tracker identity and unsaved-change rollback assertions.</returns>
    [Theory]
    [InlineData(ParticipationStatus.None)]
    [InlineData(ParticipationStatus.Following)]
    [InlineData(ParticipationStatus.Joined)]
    public async Task ExactPair_ReturnsTrackedStateWithoutSaving(ParticipationStatus status)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var row = new QuestParticipation { QuestId = seed.Quest.Id, UserId = seed.User.Id, Status = status, ChangedUtc = FoundationSeed.Now };
        await FoundationSeed.PersistAsync(database, row);
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            await db.LockEventAsync(seed.Event.Id);
            Assert.Null(await db.FindQuestParticipationForUpdateAsync(seed.Quest.Id, seed.Other.Id));
            Assert.Null(await db.FindQuestParticipationForUpdateAsync(Guid.NewGuid(), seed.User.Id));
            var current = await db.FindQuestParticipationForUpdateAsync(seed.Quest.Id, seed.User.Id);
            Assert.NotNull(current);
            Assert.Equal(row.Id, current.Id);
            Assert.Equal(row.Version, current.Version);
            Assert.Equal(status, current.Status);
            Assert.Equal(row.ChangedUtc, current.ChangedUtc);
            Assert.Same(current, Assert.Single(db.ChangeTracker.Entries()).Entity);
            current.ChangedUtc = FoundationSeed.Now.AddDays(1);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        var retained = await read.Participations.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Version, retained.Version);
        Assert.Equal(row.ChangedUtc, retained.ChangedUtc);
        Assert.Equal(status, retained.Status);
    }

    /// <summary>Cancellation interrupts a real held participation range and caller rollback permits fresh acquisition without inserting anything.</summary>
    /// <returns>Completion after a DMV-observed SQL wait, propagated cancellation and successful reacquisition.</returns>
    [Fact]
    public async Task BlockedReservation_CanBeCancelledAndReacquired()
    {
        var questId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var holder = database.CreateContext();
        await using var held = await holder.BeginTransactionAsync();
        Assert.Null(await holder.FindQuestParticipationForUpdateAsync(questId, userId, deadline.Token));
        await using var waiter = database.CreateContext();
        await using var waiting = await waiter.BeginTransactionAsync();
        var holderId = ((SqlConnection)holder.Database.GetDbConnection()).ServerProcessId;
        var waiterId = ((SqlConnection)waiter.Database.GetDbConnection()).ServerProcessId;
        var blocked = waiter.FindQuestParticipationForUpdateAsync(questId, userId, cancellation.Token);
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, waiterId, holderId, deadline.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            await waiting.RollbackAsync();
            await held.RollbackAsync();
            Assert.Empty(waiter.ChangeTracker.Entries());
            Assert.Empty(holder.ChangeTracker.Entries());
        }
        finally
        {
            cancellation.Cancel();
            await held.DisposeAsync();
            await Record.ExceptionAsync(() => blocked);
        }
        await using var fresh = database.CreateContext();
        await using var transaction = await fresh.BeginTransactionAsync();
        Assert.Null(await fresh.FindQuestParticipationForUpdateAsync(questId, userId, deadline.Token));
    }
}
