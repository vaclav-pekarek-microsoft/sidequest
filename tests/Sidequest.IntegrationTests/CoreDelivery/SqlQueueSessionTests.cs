using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Background;
using Sidequest.Infrastructure.Persistence;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreDelivery;

/// <summary>Checks queue claims against database snapshot-read settings and an explicitly established connection-session isolation level.</summary>
public sealed class SqlQueueSessionTests
{
    /// <summary>A claim must establish compatible locking independently of the database read default and a previously configured session.</summary>
    /// <param name="category">The durable queue whose table and returned ownership are checked.</param>
    /// <param name="snapshotReads">Whether the uniquely owned database uses read-committed snapshot isolation.</param>
    /// <param name="serializableSession">Whether the connection opens with Serializable rather than ReadCommitted session isolation.</param>
    /// <returns>Completion after the intended row is claimed once and completed with its exact ownership proof.</returns>
    [Theory]
    [InlineData("outbox", false, false)]
    [InlineData("outbox", true, false)]
    [InlineData("outbox", false, true)]
    [InlineData("outbox", true, true)]
    [InlineData("scheduled", false, false)]
    [InlineData("scheduled", true, false)]
    [InlineData("scheduled", false, true)]
    [InlineData("scheduled", true, true)]
    [InlineData("delivery", false, false)]
    [InlineData("delivery", true, false)]
    [InlineData("delivery", false, true)]
    [InlineData("delivery", true, true)]
    public async Task ClaimEstablishesCompatibleIsolation(string category, bool snapshotReads, bool serializableSession)
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        var id = await SeedWorkAsync(scenario, category);
        await ConfigureSnapshotReadsAsync(scenario.Database, snapshotReads);

        var queue = new SqlWorkQueue(new ObservedContextFactory(scenario.Database, new SessionIsolation(serializableSession)),
            scenario.Clock, scenario.Options);
        WorkLease? claimed;
        try
        {
            claimed = await queue.ClaimAsync(category);
        }
        catch (SqlException failure)
        {
            throw new InvalidOperationException($"Queue claim failed with SQL number {failure.Number}.");
        }
        var lease = Assert.IsType<WorkLease>(claimed);
        Assert.Equal(id, lease.Id);
        Assert.Equal(category, lease.Category);
        Assert.Equal(1, lease.Attempts);
        Assert.NotEqual(Guid.Empty, lease.Token);
        Assert.Null(await queue.ClaimAsync(category));
        Assert.True(await queue.CompleteAsync(lease));
        await using var read = scenario.Database.CreateContext();
        var completed = category switch
        {
            "outbox" => await read.OutboxMessages.Select(x => new { x.Id, x.Status, x.Attempts, x.LeaseId, x.LeaseUntilUtc }).SingleAsync(),
            "scheduled" => await read.ScheduledWork.Select(x => new { x.Id, x.Status, x.Attempts, x.LeaseId, x.LeaseUntilUtc }).SingleAsync(),
            "delivery" => await read.NotificationDeliveries.Select(x => new { x.Id, x.Status, x.Attempts, x.LeaseId, x.LeaseUntilUtc }).SingleAsync(),
            _ => throw new ArgumentException("Unsupported test queue.", nameof(category))
        };
        Assert.Equal(id, completed.Id);
        Assert.Equal(WorkStatus.Completed, completed.Status);
        Assert.Equal(1, completed.Attempts);
        Assert.Null(completed.LeaseId);
        Assert.Null(completed.LeaseUntilUtc);
    }

    /// <summary>A real single-connection pool can be reused for queue work after a Serializable application transaction, without an interceptor changing isolation.</summary>
    /// <param name="snapshotReads">Whether the owned database uses read-committed snapshot reads.</param>
    /// <returns>Completion after physical-session reuse, exclusive claim, and persisted completion are verified; only this fixture's pool is cleared.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimAfterSerializableTransactionReusesOwnedConnectionPool(bool snapshotReads)
    {
        await using var scenario = await DeliveryScenario.CreateAsync();
        var id = await scenario.AddChangeAsync();
        await ConfigureSnapshotReadsAsync(scenario.Database, snapshotReads);
        await using var template = scenario.Database.CreateContext();
        var builder = new SqlConnectionStringBuilder(template.Database.GetConnectionString())
        {
            Pooling = true,
            MaxPoolSize = 1
        };
        using var poolKey = new SqlConnection(builder.ConnectionString);
        try
        {
            await using var services = new ServiceCollection()
                .AddDbContextFactory<SidequestDbContext>(options => options.UseSqlServer(builder.ConnectionString))
                .BuildServiceProvider();
            var contexts = services.GetRequiredService<IDbContextFactory<SidequestDbContext>>();
            short originalSession;
            await using (var priming = await contexts.CreateDbContextAsync())
            {
                await using var transaction = await priming.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                originalSession = await priming.Database.SqlQueryRaw<short>("SELECT @@SPID AS [Value]").SingleAsync();
                await transaction.CommitAsync();
            }
            await using (var reused = await contexts.CreateDbContextAsync())
                Assert.Equal(originalSession, await reused.Database.SqlQueryRaw<short>("SELECT @@SPID AS [Value]").SingleAsync());

            var queue = new SqlWorkQueue(new SidequestDbContextFactory(contexts), scenario.Clock, scenario.Options);
            var lease = Assert.IsType<WorkLease>(await queue.ClaimAsync("outbox"));
            Assert.Equal(id, lease.Id);
            Assert.Equal(1, lease.Attempts);
            Assert.Null(await queue.ClaimAsync("outbox"));
            Assert.True(await queue.CompleteAsync(lease));
            await using var read = scenario.Database.CreateContext();
            var completed = await read.OutboxMessages.SingleAsync();
            Assert.Equal(id, completed.Id);
            Assert.Equal(WorkStatus.Completed, completed.Status);
            Assert.Equal(1, completed.Attempts);
            Assert.Null(completed.LeaseId);
            Assert.Null(completed.LeaseUntilUtc);
        }
        finally
        {
            SqlConnection.ClearPool(poolKey);
        }
    }

    private static async Task<Guid> SeedWorkAsync(DeliveryScenario scenario, string category)
    {
        switch (category)
        {
            case "outbox":
                return await scenario.AddChangeAsync();
            case "scheduled":
                var work = new ScheduledWork { Type = "session-test.v1", DeduplicationKey = "session-test", DueUtc = scenario.Clock.Now };
                await FoundationSeed.PersistAsync(scenario.Database, work);
                return work.Id;
            case "delivery":
                await scenario.AddChangeAsync();
                await scenario.ProcessChangeAsync();
                await using (var read = scenario.Database.CreateContext())
                    return (await read.NotificationDeliveries.SingleAsync()).Id;
            default:
                throw new ArgumentException("Unsupported test queue.", nameof(category));
        }
    }

    private static async Task ConfigureSnapshotReadsAsync(SqlTestDatabase fixture, bool enabled)
    {
        await using var template = fixture.CreateContext();
        var builder = new SqlConnectionStringBuilder(template.Database.GetConnectionString());
        var database = builder.InitialCatalog;
        if (!database.StartsWith("SidequestTests_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(database["SidequestTests_".Length..], "N", out _))
            throw new InvalidOperationException("Snapshot configuration requires the uniquely owned fixture database.");
        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{database}] SET READ_COMMITTED_SNAPSHOT {(enabled ? "ON" : "OFF")} WITH ROLLBACK IMMEDIATE;";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @name;";
        command.Parameters.Add(new SqlParameter("@name", database));
        Assert.Equal(enabled, Assert.IsType<bool>(await command.ExecuteScalarAsync()));
    }

    private sealed class SessionIsolation(bool serializable) : DbConnectionInterceptor
    {
        /// <inheritdoc />
        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = serializable
                ? "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;"
                : "SET TRANSACTION ISOLATION LEVEL READ COMMITTED;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
