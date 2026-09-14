using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Infrastructure.Persistence;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreBoundaries;

/// <summary>Exercises save and failed-transaction interception with provider exceptions captured from genuine isolated SQL operations.</summary>
/// <param name="database">The existing migrated SQL fixture; no private SqlException construction or synthetic provider is used.</param>
public sealed class CapturedFailureBoundaryTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Checks every save overload normalizes nested real deadlock/duplicate failures but preserves unrelated SQL and cancellation chains.</summary>
    /// <returns>A task completing after real error capture, exact error identity/content, one invocation and no-durable-write assertions.</returns>
    [Fact]
    public async Task AllSaveOverloads_CapturedProviderFailures_NormalizeWithoutRetryOrWrite()
    {
        var deadlock = Assert.IsType<SqlException>(await RealDeadlock.RunAsync(database, "ado"));
        Assert.Equal(1205, deadlock.Number);
        var seed = await FoundationSeed.CreateAsync(database);
        var duplicate = await CaptureAsync("""
            INSERT INTO [Users] ([Id], [TenantId], [ObjectId], [DisplayName], [Email], [IsEligible])
            SELECT NEWID(), [TenantId], [ObjectId], N'Duplicate', N'duplicate@example.invalid', 1 FROM [Users] WHERE [Id] = @id
            """, seed.User.Id);
        Assert.Contains(duplicate.Number, new[] { 2601, 2627 });
        var unrelated = await CaptureAsync("SELECT * FROM [CoreBoundaryMissingTable]", seed.User.Id);
        Assert.Equal(208, unrelated.Number);
        foreach (var route in new[] { "sync", "syncTrue", "syncFalse", "async", "token", "asyncTrue", "asyncFalse" })
        {
            foreach (var kind in new[] { "deadlock", "duplicate", "unrelated", "cancelAbove", "cancelBelow" })
            {
                var pending = FoundationSeed.NewUser();
                Exception cause = kind switch
                {
                    "deadlock" => deadlock,
                    "duplicate" => duplicate,
                    "unrelated" => unrelated,
                    "cancelAbove" => new OperationCanceledException("cancel", deadlock),
                    "cancelBelow" => new DbUpdateConcurrencyException("race", new OperationCanceledException()),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind))
                };
                var original = new InvalidOperationException("CB-PRIVATE-DETAIL", new DbUpdateException("save wrapper", cause));
                await using var db = database.CreateContext();
                db.Users.Add(pending);
                var calls = 0;
                db.SavingChanges += (_, _) => { calls++; throw original; };
                var error = await Record.ExceptionAsync(() => SaveAsync(db, route));
                if (kind is "deadlock" or "duplicate")
                    SqlBoundaryCoordinator.AssertSafeConflict(error, kind == "deadlock" ? RealDeadlock.Message : FoundationSeed.DuplicateMessage);
                else
                    Assert.Same(original, error);
                Assert.Equal(1, calls);
                Assert.Equal(EntityState.Added, db.Entry(pending).State);
                Assert.Empty(pending.Version);
                Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
                await using var read = database.CreateContext();
                Assert.False(await read.Users.AnyAsync(x => x.Id == pending.Id));
                Assert.Equal(seed.User.Version, (await read.Users.SingleAsync(x => x.Id == seed.User.Id)).Version);
            }
        }
    }

    /// <summary>Checks the registered transaction interceptor through UseTransaction, with a real captured1205 and an explicitly throwing completion wrapper.</summary>
    /// <param name="async">Whether completion uses the asynchronous EF transaction API.</param>
    /// <param name="commit">Whether the completion failure is raised from commit rather than rollback.</param>
    /// <returns>A task completing after safe/unchanged error partitions, one completion call, explicit underlying rollback and durable-state verification.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TransactionCompletion_CapturedFailures_PreserveBoundaryAndRollbackState(bool @async, bool commit)
    {
        var deadlock = Assert.IsType<SqlException>(await RealDeadlock.RunAsync(database, "ado"));
        Assert.Equal(1205, deadlock.Number);
        var unrelated = await CaptureAsync("SELECT * FROM [CoreBoundaryMissingTable]", Guid.NewGuid());
        foreach (var kind in new[] { "direct", "nested", "unrelated", "cancel" })
        {
            var seed = await FoundationSeed.CreateAsync(database);
            Exception original = kind switch
            {
                "direct" => deadlock,
                "nested" => new InvalidOperationException("CB-PRIVATE-DETAIL", new DbUpdateException("wrapper", deadlock)),
                "unrelated" => unrelated,
                "cancel" => new InvalidOperationException("outer", new OperationCanceledException("cancel", deadlock)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            await using var connection = SqlBoundaryCoordinator.Connection(database);
            await connection.OpenAsync();
            await using var underlying = (SqlTransaction)await connection.BeginTransactionAsync();
            Assert.Equal(1, await SqlBoundaryCoordinator.ExecuteAsync(connection, underlying,
                "UPDATE [Events] SET [Name] = N'Uncommitted completion change' WHERE [Id] = @id", seed.Event.Id));
            await using var wrapper = new ThrowingCompletionTransaction(underlying, original);
            await using var db = new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>()
                .UseSqlServer(connection).Options);
            await using var transaction = await db.Database.UseTransactionAsync(wrapper);
            Assert.NotNull(transaction);
            var error = await Record.ExceptionAsync(async () =>
            {
                if (@async)
                {
                    if (commit)
                        await transaction.CommitAsync();
                    else
                        await transaction.RollbackAsync();
                }
                else if (commit)
                    transaction.Commit();
                else
                    transaction.Rollback();
            });
            if (kind is "direct" or "nested")
                SqlBoundaryCoordinator.AssertSafeConflict(error, RealDeadlock.Message);
            else
                Assert.Same(original, error);
            Assert.Equal(1, wrapper.Calls);
            Assert.Empty(db.ChangeTracker.Entries());
            await underlying.RollbackAsync();
            await using var read = database.CreateContext();
            var stored = await read.Events.SingleAsync(x => x.Id == seed.Event.Id);
            Assert.Equal("Sensitive Event sentinel", stored.Name);
            Assert.Equal(seed.Event.Version, stored.Version);
        }
    }

    private async Task<SqlException> CaptureAsync(string sql, Guid id)
    {
        await using var connection = SqlBoundaryCoordinator.Connection(database);
        await connection.OpenAsync();
        return await Assert.ThrowsAsync<SqlException>(
            () => SqlBoundaryCoordinator.ExecuteAsync(connection, null, sql, id));
    }

    private static Task<int> SaveAsync(SidequestDbContext db, string route) => route switch
    {
        "sync" => Task.FromResult(db.SaveChanges()),
        "syncTrue" => Task.FromResult(db.SaveChanges(true)),
        "syncFalse" => Task.FromResult(db.SaveChanges(false)),
        "async" => db.SaveChangesAsync(),
        "token" => db.SaveChangesAsync(CancellationToken.None),
        "asyncTrue" => db.SaveChangesAsync(true, CancellationToken.None),
        "asyncFalse" => db.SaveChangesAsync(false, CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };
}
