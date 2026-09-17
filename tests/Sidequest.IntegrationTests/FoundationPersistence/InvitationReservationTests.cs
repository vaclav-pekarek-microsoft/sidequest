using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreBoundaries;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks invitation reservation preconditions, tracked exact-key results and cooperative SQL lock cancellation.</summary>
/// <param name="database">The uniquely owned migrated SQL catalog for these boundary scenarios.</param>
public sealed class InvitationReservationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Absent or weaker transactions cannot create a reservation that expires before its caller's mutation.</summary>
    /// <param name="isolation">Null for no transaction, or the weaker isolation level to reject.</param>
    /// <returns>Completion after the exact error and unchanged transaction ownership.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task InvalidTransaction_IsRejected(IsolationLevel? isolation)
    {
        await using var db = database.CreateContext();
        await using var transaction = isolation is null ? null : await db.BeginTransactionAsync(isolation.Value);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.FindQuestInvitationForUpdateAsync(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal("An explicit Serializable transaction is required before reserving a Quest invitation.", error.Message);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Empty internal identifiers fail before transaction checks or database reads.</summary>
    /// <param name="emptyQuest">Whether to omit the Quest identifier rather than the invitee identifier.</param>
    /// <returns>Completion after the exact validation code/field and no tracked rows.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyIdentifier_IsRejected(bool emptyQuest)
    {
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            db.FindQuestInvitationForUpdateAsync(emptyQuest ? Guid.Empty : Guid.NewGuid(), emptyQuest ? Guid.NewGuid() : Guid.Empty));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal(emptyQuest ? "questId" : "userId", error.Field);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Pre-cancellation wins over identifier and transaction validation without querying.</summary>
    /// <returns>Completion after observing the original cancellation token and unchanged context state.</returns>
    [Fact]
    public async Task PreCancelled_DoesNotQuery()
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            db.FindQuestInvitationForUpdateAsync(Guid.Empty, Guid.Empty, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Both invitation states are exact tracked results; missing neighbouring pairs remain absent and unsaved edits roll back.</summary>
    /// <param name="status">The retained Active or Revoked state to return unchanged.</param>
    /// <returns>Completion after exact identity/version, tracking, neighbouring-key and rollback assertions.</returns>
    [Theory]
    [InlineData(QuestInvitationStatus.Active)]
    [InlineData(QuestInvitationStatus.Revoked)]
    public async Task ExactPair_ReturnsTrackedCurrentStateWithoutSaving(QuestInvitationStatus status)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var invitation = seed.Invitation(status);
        await FoundationSeed.PersistAsync(database, invitation);
        await using (var db = database.CreateContext())
        {
            await using var transaction = await db.BeginTransactionAsync();
            Assert.Null(await db.FindQuestInvitationForUpdateAsync(seed.Quest.Id, seed.Other.Id));
            Assert.Null(await db.FindQuestInvitationForUpdateAsync(Guid.NewGuid(), seed.User.Id));
            var current = await db.FindQuestInvitationForUpdateAsync(seed.Quest.Id, seed.User.Id);
            Assert.NotNull(current);
            Assert.Equal(invitation.Id, current.Id);
            Assert.Equal(invitation.Version, current.Version);
            Assert.Equal(status, current.Status);
            Assert.Same(current, Assert.Single(db.ChangeTracker.Entries()).Entity);
            current.ChangedUtc = FoundationSeed.Now.AddDays(1);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        var retained = await read.QuestInvitations.SingleAsync(x => x.Id == invitation.Id);
        Assert.Equal(invitation.Version, retained.Version);
        Assert.Equal(invitation.ChangedUtc, retained.ChangedUtc);
    }

    /// <summary>Cancellation interrupts a real held invitation range, and caller rollback permits a fresh reservation without writes.</summary>
    /// <returns>Completion after a DMV-observed wait, cancellation, rollback and successful reacquisition.</returns>
    [Fact]
    public async Task BlockedReservation_CanBeCancelledAndReacquired()
    {
        var questId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var holder = database.CreateContext();
        await using var held = await holder.BeginTransactionAsync();
        Assert.Null(await holder.FindQuestInvitationForUpdateAsync(questId, userId, deadline.Token));
        await using var waiter = database.CreateContext();
        await using var waiting = await waiter.BeginTransactionAsync();
        var holderId = ((SqlConnection)holder.Database.GetDbConnection()).ServerProcessId;
        var waiterId = ((SqlConnection)waiter.Database.GetDbConnection()).ServerProcessId;
        var blocked = waiter.FindQuestInvitationForUpdateAsync(questId, userId, cancellation.Token);
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
        Assert.Null(await fresh.FindQuestInvitationForUpdateAsync(questId, userId, deadline.Token));
    }
}
