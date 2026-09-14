using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Sidequest.Infrastructure.Persistence;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreBoundaries;

/// <summary>Uses real two-session SQL deadlocks to exercise production query/command interception and atomic victim rollback.</summary>
/// <param name="database">The existing migrated, uniquely owned SQL database fixture.</param>
public sealed class SqlDeadlockBoundaryTests(SqlTestDatabase database) : IClassFixture<SqlTestDatabase>
{
    /// <summary>Checks final public errors are safe Conflict rather than provider or EF wrapper errors, without retry or partial victim writes.</summary>
    /// <param name="route">The synchronous/asynchronous EF query or raw-command entry point chosen as the low-priority victim.</param>
    /// <returns>A task completing after a DMV-observed wait, genuine deadlock, committed winner and rolled-back victim assertions.</returns>
    [Theory]
    [InlineData("querySync")]
    [InlineData("queryAsync")]
    [InlineData("commandSync")]
    [InlineData("commandAsync")]
    public async Task GenuineDeadlock_PublicQueryAndCommand_ReturnSafeConflict(string route)
    {
        SqlBoundaryCoordinator.AssertSafeConflict(await RealDeadlock.RunAsync(database, route), RealDeadlock.Message);
    }

    /// <summary>Checks missing-table provider failures remain raw SQL208 rather than being mislabeled as a retriable conflict.</summary>
    /// <param name="async">Whether the raw command is executed asynchronously.</param>
    /// <returns>A task completing after exact provider code and unchanged durable seed content assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedRawCommandFailure_RemainsProviderError(bool @async)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        await using var db = database.CreateContext();
        var error = await Record.ExceptionAsync(async () =>
        {
            if (@async)
                await db.Database.ExecuteSqlRawAsync("SELECT * FROM [CoreBoundaryMissingTable]");
            else
                db.Database.ExecuteSqlRaw("SELECT * FROM [CoreBoundaryMissingTable]");
        });
        Assert.Equal(208, Assert.IsType<SqlException>(error).Number);
        Assert.Empty(db.ChangeTracker.Entries());
        await using var read = database.CreateContext();
        Assert.Equal(seed.Event.Version, (await read.Events.SingleAsync(x => x.Id == seed.Event.Id)).Version);
    }

    /// <summary>Checks unrelated errors on both query execution paths retain their provider classification rather than becoming Conflict.</summary>
    /// <param name="async">Whether query materialization uses the asynchronous API.</param>
    /// <returns>A task completing after the exact208 error and no-tracked-entity assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedQueryFailure_RemainsProviderError(bool @async)
    {
        await using var db = database.CreateContext();
        var error = await Record.ExceptionAsync(async () =>
        {
            var query = db.Events.FromSqlRaw("SELECT * FROM [CoreBoundaryMissingTable]").AsNoTracking();
            if (@async)
                await query.ToListAsync();
            else
                query.ToList();
        });
        Assert.Equal(208, Assert.IsType<SqlException>(error).Number);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    /// <summary>Checks the production reader decorator preserves real SQL row order, later-result metadata/values, provider errors, and disposal.</summary>
    /// <param name="asynchronous">Whether execution, cursor navigation and disposal use asynchronous APIs.</param>
    /// <returns>A task completing after both result sets, exact typed values and null/binary data, ordinary getter errors, and closed-reader assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_MultipleResults_PreservesValuesMetadataAndLifetime(bool asynchronous)
    {
        await using var db = database.CreateContext();
        var command = db.GetService<IRawSqlCommandBuilder>().Build("""
            SELECT [Number], [Label] FROM (VALUES (7, N'first'), (9, N'second')) AS rows([Number], [Label])
            ORDER BY [Number];
            SELECT CAST(4294967301 AS bigint) AS [Revision],
                   CAST(NULL AS nvarchar(20)) AS [Optional],
                   CAST(0x0102030405060708 AS varbinary(8)) AS [Version];
            """);
        var parameters = new RelationalCommandParameterObject(db.GetService<IRelationalConnection>(),
            null, null, db, db.GetService<IRelationalCommandDiagnosticsLogger>());
        var results = asynchronous
            ? await command.ExecuteReaderAsync(parameters)
            : command.ExecuteReader(parameters);
        var reader = results.DbDataReader;
        try
        {
            Assert.Equal(2, reader.FieldCount);
            Assert.Equal(new[] { "Number", "Label" }, reader.GetColumnSchema().Select(x => x.ColumnName));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(7, reader.GetInt32(0));
            Assert.Equal("first", reader.GetString(reader.GetOrdinal("Label")));
            Assert.Throws<InvalidCastException>(() => reader.GetInt32(1));
            Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("MissingColumn"));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(9, reader.GetInt32(0));
            Assert.Equal("second", reader.GetString(1));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.Equal(3, reader.FieldCount);
            Assert.Equal(new[] { "Revision", "Optional", "Version" }, reader.GetColumnSchema().Select(x => x.ColumnName));
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal(4294967301L, reader.GetInt64(0));
            Assert.True(asynchronous ? await reader.IsDBNullAsync(1) : reader.IsDBNull(1));
            Assert.Equal(DBNull.Value, reader["Optional"]);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, asynchronous
                ? await reader.GetFieldValueAsync<byte[]>(2)
                : reader.GetFieldValue<byte[]>(2));
            Assert.False(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            if (asynchronous)
                await results.DisposeAsync();
            else
                results.Dispose();
        }
        Assert.True(reader.IsClosed);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    /// <summary>Checks cancellation interrupts an observed raw-command lock wait without a Conflict, retry or partial write.</summary>
    /// <returns>A task completing after DMV blocking evidence, cancellation and unchanged independent SQL state.</returns>
    [Fact]
    public async Task RawCommand_BlockedCancellation_PreservesProviderCancellationAndNoWrite()
    {
        var seed = await FoundationSeed.CreateAsync(database);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using var holder = SqlBoundaryCoordinator.Connection(database);
        await holder.OpenAsync(deadline.Token);
        var holderId = await SqlBoundaryCoordinator.SessionAsync(holder);
        await using var held = (SqlTransaction)await holder.BeginTransactionAsync(deadline.Token);
        Assert.Equal(1, await SqlBoundaryCoordinator.ExecuteAsync(holder, held,
            "UPDATE [Events] SET [Name] = N'Uncommitted holder' WHERE [Id] = @id", seed.Event.Id, deadline.Token));
        var probe = new BoundaryCommandProbe();
        await using var waiterConnection = SqlBoundaryCoordinator.Connection(database);
        await using var waiter = new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>()
            .UseSqlServer(waiterConnection).AddInterceptors(probe).Options);
        await waiter.Database.OpenConnectionAsync(deadline.Token);
        var waiterId = await SqlBoundaryCoordinator.SessionAsync((SqlConnection)waiter.Database.GetDbConnection());
        var parameter = new SqlParameter("@id", seed.Event.Id);
        var waiting = waiter.Database.ExecuteSqlRawAsync(
            "UPDATE [Events] SET [Description] = N'Cancelled command' WHERE [Id] = @id /* CB-VICTIM */", [parameter], cancellation.Token);
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, waiterId, holderId, deadline.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            var error = await Record.ExceptionAsync(async () => await waiting);
            if (error is SqlException provider)
            {
                Assert.Contains(provider.Errors.Cast<SqlError>(), item => item.Number == 0);
                Assert.DoesNotContain(provider.Errors.Cast<SqlError>(), item => item.Number is 1205 or 2601 or 2627);
            }
            else
                Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(1, probe.Calls);
            Assert.Equal(1, probe.Cancellations);
            Assert.Empty(waiter.ChangeTracker.Entries());
            await held.RollbackAsync(deadline.Token);
            await using var read = database.CreateContext();
            var row = await read.Events.SingleAsync(x => x.Id == seed.Event.Id);
            Assert.Equal("Sensitive Event sentinel", row.Name);
            Assert.Equal("Private event details", row.Description);
            Assert.Equal(seed.Event.Version, row.Version);
        }
        finally
        {
            cancellation.Cancel();
            await held.DisposeAsync();
            await Record.ExceptionAsync(async () => await waiting);
        }
    }
}
