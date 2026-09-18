using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;
using Sidequest.IntegrationTests.CoreBoundaries;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Checks exact scheduled-key validation, literal matching and caller-owned reservation semantics on real SQL.</summary>
/// <param name="database">The uniquely owned migrated catalog for these boundary checks.</param>
public sealed class ExactWorkReservationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Missing and weaker transactions cannot silently acquire a short-lived exact-key reservation.</summary>
    /// <param name="isolation">Null for no explicit transaction, otherwise a weaker isolation level.</param>
    /// <returns>Completion after the precise precondition failure and unchanged tracking/transaction ownership.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    public async Task InvalidTransaction_IsRejected(IsolationLevel? isolation)
    {
        await using var db = database.CreateContext();
        await using var transaction = isolation is null ? null : await db.BeginTransactionAsync(isolation.Value);
        ISidequestDbContext boundary = db;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => boundary.HasScheduledWorkForUpdateAsync("key"));
        Assert.Equal("An explicit Serializable transaction is required before reserving scheduled work.", error.Message);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Same(transaction, db.Database.CurrentTransaction);
    }

    /// <summary>Invalid complete keys fail explicitly before querying or requiring a transaction.</summary>
    /// <param name="partition">Null, empty, whitespace-only, or a key longer than the mapped limit.</param>
    /// <returns>Completion after exact validation code/field and absence of tracked writes.</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("overlong")]
    public async Task InvalidKey_IsRejected(string partition)
    {
        var key = partition switch { "null" => null!, "empty" => "", "whitespace" => " ", _ => new string('x', 301) };
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(() => db.HasScheduledWorkForUpdateAsync(key));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Equal("deduplicationKey", error.Field);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Cancellation precedes key and transaction validation without creating a context transaction.</summary>
    /// <returns>Completion after observing the original cancellation token and no tracked writes.</returns>
    [Fact]
    public async Task PreCancelled_DoesNotQuery()
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => db.HasScheduledWorkForUpdateAsync("", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Cancellation interrupts a real exact-key lock wait without writes, and rollback leaves the key available to a fresh caller.</summary>
    /// <returns>Completion after a DMV-observed wait, caller cancellation, transaction release, and a fresh absent-key reservation.</returns>
    [Fact]
    public async Task BlockedReservation_CanBeCancelledAndReacquired()
    {
        var key = $"cancel:{Guid.NewGuid():N}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var holder = database.CreateContext();
        await using var held = await holder.BeginTransactionAsync();
        Assert.False(await holder.HasScheduledWorkForUpdateAsync(key, deadline.Token));
        await using var waiter = database.CreateContext();
        await using var waiting = await waiter.BeginTransactionAsync();
        var holderId = ((SqlConnection)holder.Database.GetDbConnection()).ServerProcessId;
        var waiterId = ((SqlConnection)waiter.Database.GetDbConnection()).ServerProcessId;
        var blocked = waiter.HasScheduledWorkForUpdateAsync(key, cancellation.Token);
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
        Assert.False(await fresh.HasScheduledWorkForUpdateAsync(key, deadline.Token));
        Assert.False(await fresh.ScheduledWork.AnyAsync(x => x.DeduplicationKey == key, deadline.Token));
    }

    /// <summary>One-character, maximum-length, and SQL-significant keys are exact database reads, not prefix matches or implicit tracked-entity saves.</summary>
    /// <param name="partition">The short, maximum-length, or literal SQL-character key partition.</param>
    /// <returns>Completion after false before save, true for saved terminal work, unchanged tracking, and complete caller rollback.</returns>
    [Theory]
    [InlineData("short")]
    [InlineData("maximum")]
    [InlineData("literal")]
    public async Task ExactKey_IsLiteralAndCallerOwned(string partition)
    {
        var key = partition switch
        {
            "short" => "x", "maximum" => new string('a', 268) + Guid.NewGuid().ToString("N"),
            _ => $"key:{Guid.NewGuid():N}:%_['"
        };
        var lookalike = key.Length == 300 ? key[..^1] : key + ":attempt";
        var decoy = new ScheduledWork { Type = WorkTypes.EventCompletion, DeduplicationKey = lookalike, DueUtc = FoundationSeed.Now };
        await FoundationSeed.PersistAsync(database, decoy);
        var staged = new ScheduledWork
        {
            Type = WorkTypes.EventCompletion, DeduplicationKey = key, Status = WorkStatus.Completed, DueUtc = FoundationSeed.Now
        };
        await using (var db = database.CreateContext())
        {
            db.ScheduledWork.Add(staged);
            await using var transaction = await db.BeginTransactionAsync();
            Assert.False(await db.HasScheduledWorkForUpdateAsync(key));
            var entry = Assert.Single(db.ChangeTracker.Entries());
            Assert.Same(staged, entry.Entity);
            Assert.Equal(EntityState.Added, entry.State);
            Assert.Empty(staged.Version);
            Assert.Same(transaction, db.Database.CurrentTransaction);
            await db.SaveChangesAsync();
            Assert.True(await db.HasScheduledWorkForUpdateAsync(key));
            Assert.Single(db.ChangeTracker.Entries());
            await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        Assert.False(await read.ScheduledWork.AnyAsync(x => x.Id == staged.Id));
        Assert.Equal(decoy.Version, (await read.ScheduledWork.SingleAsync(x => x.Id == decoy.Id)).Version);
    }
}
