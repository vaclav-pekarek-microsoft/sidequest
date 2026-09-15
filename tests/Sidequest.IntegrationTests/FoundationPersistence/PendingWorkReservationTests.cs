using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Application.Abstractions;
using Sidequest.Domain.Model;
using Sidequest.Domain.Rules;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Verifies scheduling existence semantics and transaction-owned write-intent reservations against real SQL.</summary>
/// <param name="database">The class-owned migrated SQL catalog; no shared development database is modified.</param>
public sealed class PendingWorkReservationTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>A second scheduler cannot take a compatible shared reservation and later deadlock while both insert into the same absent range.</summary>
    /// <returns>Completion after a real competing lock timeout, first commit, and a fresh positive deduplication read.</returns>
    [Fact]
    public async Task AbsentRange_ReservesWriteIntentUntilCallerCommits()
    {
        var prefix = $"reserve:{Guid.NewGuid():N}:";
        await using var first = database.CreateContext();
        await using var firstTransaction = await first.BeginTransactionAsync();
        Assert.False(await first.HasPendingScheduledWorkForUpdateAsync(prefix));
        Assert.Empty(first.ChangeTracker.Entries());
        Assert.Equal(IsolationLevel.Serializable, firstTransaction.GetDbTransaction().IsolationLevel);

        await using var second = database.CreateContext();
        await using var secondTransaction = await second.BeginTransactionAsync();
        await second.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 500");
        var blocked = await Assert.ThrowsAsync<SqlException>(() =>
            second.HasPendingScheduledWorkForUpdateAsync(prefix));
        Assert.Equal(1222, blocked.Number);
        await secondTransaction.RollbackAsync();

        first.ScheduledWork.Add(Work(prefix + "one", WorkStatus.Pending));
        await first.SaveChangesAsync();
        await firstTransaction.CommitAsync();
        await using var retry = database.CreateContext();
        await using var retryTransaction = await retry.BeginTransactionAsync();
        Assert.True(await retry.HasPendingScheduledWorkForUpdateAsync(prefix));
        Assert.Single(await retry.ScheduledWork.Where(x => x.DeduplicationKey.StartsWith(prefix)).ToListAsync());
    }

    /// <summary>Only live matching work suppresses a new completion intent; unrelated work and terminal attempts do not.</summary>
    /// <param name="status">Persisted work state being checked.</param>
    /// <param name="expected">Whether the existing attempt must suppress insertion.</param>
    /// <returns>Completion after exact prefix/state evaluation without tracked entities or writes.</returns>
    [Theory]
    [InlineData(WorkStatus.Pending, true)]
    [InlineData(WorkStatus.Processing, true)]
    [InlineData(WorkStatus.Completed, false)]
    [InlineData(WorkStatus.DeadLetter, false)]
    [InlineData(WorkStatus.Superseded, false)]
    public async Task ExistingWork_MatchesOnlyLivePrefix(WorkStatus status, bool expected)
    {
        var prefix = $"state:{Guid.NewGuid():N}:";
        await using (var seed = database.CreateContext())
        {
            seed.ScheduledWork.AddRange(Work(prefix + "attempt", status), Work($"unrelated:{Guid.NewGuid():N}", WorkStatus.Pending));
            await seed.SaveChangesAsync();
        }
        await using var db = database.CreateContext();
        await using var transaction = await db.BeginTransactionAsync();
        Assert.Equal(expected, await db.HasPendingScheduledWorkForUpdateAsync(prefix));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>SQL wildcard and quote characters in a prefix remain literal rather than broadening its range.</summary>
    /// <param name="literal">A SQL-significant sequence in the requested prefix.</param>
    /// <param name="lookalike">Text that must not match that literal sequence.</param>
    /// <returns>Completion after rejecting a lookalike and finding a subsequently persisted literal key.</returns>
    [Theory]
    [InlineData("%", "other")]
    [InlineData("_", "x")]
    [InlineData("[ab]", "a")]
    [InlineData("'", "x")]
    public async Task PrefixCharacters_AreLiteral(string literal, string lookalike)
    {
        var stem = $"literal:{Guid.NewGuid():N}:";
        var prefix = stem + literal;
        await using var db = database.CreateContext();
        db.ScheduledWork.Add(Work(stem + lookalike + ":attempt", WorkStatus.Pending));
        await db.SaveChangesAsync();
        await using var transaction = await db.BeginTransactionAsync();
        Assert.False(await db.HasPendingScheduledWorkForUpdateAsync(prefix));
        db.ScheduledWork.Add(Work(prefix + ":attempt", WorkStatus.Pending));
        await db.SaveChangesAsync();
        Assert.True(await db.HasPendingScheduledWorkForUpdateAsync(prefix));
        await transaction.RollbackAsync();
        Assert.False(await db.ScheduledWork.AsNoTracking().AnyAsync(x => x.DeduplicationKey == prefix + ":attempt"));
    }

    /// <summary>The maximum stored-key length is a valid exact prefix; reserving it does not commit caller changes.</summary>
    /// <returns>Completion after matching a 300-character key and rolling back its staged insertion.</returns>
    [Fact]
    public async Task MaximumPrefix_IsAcceptedWithoutImplicitCommit()
    {
        var prefix = new string('a', 268) + Guid.NewGuid().ToString("N");
        await using var db = database.CreateContext();
        await using (var transaction = await db.BeginTransactionAsync())
        {
            Assert.False(await db.HasPendingScheduledWorkForUpdateAsync(prefix));
            db.ScheduledWork.Add(Work(prefix, WorkStatus.Pending));
            await db.SaveChangesAsync();
            Assert.True(await db.HasPendingScheduledWorkForUpdateAsync(prefix));
            await transaction.RollbackAsync();
        }
        await using var read = database.CreateContext();
        Assert.False(await read.ScheduledWork.AnyAsync(x => x.DeduplicationKey == prefix));
    }

    /// <summary>Invalid prefixes fail explicitly before acquiring a transaction or querying SQL.</summary>
    /// <param name="partition">Null, empty, whitespace, or a key exceeding the mapped limit.</param>
    /// <returns>Completion after exact validation category and absence of tracked writes.</returns>
    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("overlong")]
    public async Task InvalidPrefix_IsRejected(string partition)
    {
        var prefix = partition switch { "null" => null!, "empty" => "", "whitespace" => " ", _ => new string('x', 301) };
        await using var db = database.CreateContext();
        var error = await Assert.ThrowsAsync<DomainException>(() => db.HasPendingScheduledWorkForUpdateAsync(prefix));
        Assert.Equal(ErrorCode.Validation, error.Code);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>A reservation cannot silently use an implicit or weaker transaction.</summary>
    /// <param name="readCommitted">Whether a caller supplied an explicitly weaker isolation level.</param>
    /// <returns>Completion after the precise transaction precondition failure.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWeakerTransaction_IsRejected(bool readCommitted)
    {
        await using var db = database.CreateContext();
        await using var transaction = readCommitted ? await db.BeginTransactionAsync(IsolationLevel.ReadCommitted) : null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.HasPendingScheduledWorkForUpdateAsync("key:"));
        Assert.Equal("An explicit Serializable transaction is required before reserving scheduled work.", error.Message);
    }

    /// <summary>Caller cancellation takes precedence over database work and does not create a transaction.</summary>
    /// <returns>Completion after observing the original cancellation token.</returns>
    [Fact]
    public async Task CancelledReservation_DoesNotQuery()
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            db.HasPendingScheduledWorkForUpdateAsync("key:", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static ScheduledWork Work(string key, WorkStatus status) => new()
    {
        DeduplicationKey = key, Status = status, Type = WorkTypes.QuestCompletion,
        DueUtc = DateTimeOffset.Parse("2026-07-15T14:00:00Z"), PayloadJson = "{}"
    };
}
