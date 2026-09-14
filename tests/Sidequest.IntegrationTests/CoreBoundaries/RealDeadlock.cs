using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Infrastructure.Persistence;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreBoundaries;

internal static class RealDeadlock
{
    internal const string Message = "This operation conflicted with another change. Reload and try again.";

    internal static async Task<Exception> RunAsync(SqlTestDatabase database, string route)
    {
        var seed = await FoundationSeed.CreateAsync(database);
        var second = FoundationSeed.NewEvent(seed.Other.Id);
        await FoundationSeed.PersistAsync(database, second);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var victimConnection = SqlBoundaryCoordinator.Connection(database);
        await using var winnerConnection = SqlBoundaryCoordinator.Connection(database);
        await victimConnection.OpenAsync(deadline.Token);
        await winnerConnection.OpenAsync(deadline.Token);
        await SqlBoundaryCoordinator.ExecuteAsync(victimConnection, null, "SET DEADLOCK_PRIORITY LOW", token: deadline.Token);
        await SqlBoundaryCoordinator.ExecuteAsync(winnerConnection, null, "SET DEADLOCK_PRIORITY HIGH", token: deadline.Token);
        var victimId = await SqlBoundaryCoordinator.SessionAsync(victimConnection);
        var winnerId = await SqlBoundaryCoordinator.SessionAsync(winnerConnection);
        await using var victimTransaction = (SqlTransaction)await victimConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
        await using var winnerTransaction = (SqlTransaction)await winnerConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted, deadline.Token);
        Assert.Equal(1, await SqlBoundaryCoordinator.ExecuteAsync(victimConnection, victimTransaction,
            "UPDATE [Events] SET [Description] = N'Victim partial mutation' WHERE [Id] = @id", seed.Event.Id, deadline.Token));
        Assert.Equal(1, await SqlBoundaryCoordinator.ExecuteAsync(winnerConnection, winnerTransaction,
            "UPDATE [Events] SET [Name] = N'Winner second' WHERE [Id] = @id", second.Id, deadline.Token));
        var winner = SqlBoundaryCoordinator.ExecuteAsync(winnerConnection, winnerTransaction,
            "UPDATE [Events] SET [Name] = N'Winner first' WHERE [Id] = @id", seed.Event.Id, deadline.Token);
        var probe = new BoundaryCommandProbe();
        await using var victim = new SidequestDbContext(new DbContextOptionsBuilder<SidequestDbContext>()
            .UseSqlServer(victimConnection, options => options.CommandTimeout(30)).AddInterceptors(probe).Options);
        await victim.Database.UseTransactionAsync(victimTransaction, deadline.Token);
        Exception? error;
        try
        {
            await SqlBoundaryCoordinator.WaitForBlockAsync(database, winnerId, victimId, deadline.Token);
            Assert.False(winner.IsCompleted);
            error = await Record.ExceptionAsync(async () =>
            {
                var parameter = new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = second.Id };
                const string query = "SELECT * FROM [Events] WITH (UPDLOCK) WHERE [Id] = @id /* CB-VICTIM */";
                const string update = "UPDATE [Events] SET [Description] = N'Victim second mutation' WHERE [Id] = @id /* CB-VICTIM */";
                switch (route)
                {
                    case "querySync":
                        victim.Events.FromSqlRaw(query, parameter).AsNoTracking().ToList();
                        break;
                    case "queryAsync":
                        await victim.Events.FromSqlRaw(query, parameter).AsNoTracking().ToListAsync(deadline.Token);
                        break;
                    case "commandSync":
                        victim.Database.ExecuteSqlRaw(update, parameter);
                        break;
                    case "commandAsync":
                        await victim.Database.ExecuteSqlRawAsync(update, [parameter], deadline.Token);
                        break;
                    case "ado":
                        await SqlBoundaryCoordinator.ExecuteAsync(victimConnection, victimTransaction, update, second.Id, deadline.Token);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(route));
                }
            });
            Assert.Equal(1, await winner);
            await winnerTransaction.CommitAsync(deadline.Token);
            Assert.Equal(route == "ado" ? 0 : 1, probe.Calls);
            Assert.Empty(victim.ChangeTracker.Entries());
        }
        finally
        {
            deadline.Cancel();
            await victimTransaction.DisposeAsync();
            await Record.ExceptionAsync(async () => await winner);
        }
        await using var read = database.CreateContext();
        var firstStored = await read.Events.SingleAsync(x => x.Id == seed.Event.Id);
        var secondStored = await read.Events.SingleAsync(x => x.Id == second.Id);
        Assert.Equal("Winner first", firstStored.Name);
        Assert.Equal("Winner second", secondStored.Name);
        Assert.Equal("Private event details", firstStored.Description);
        Assert.Equal("Private event details", secondStored.Description);
        Assert.NotEqual(seed.Event.Version, firstStored.Version);
        Assert.NotEqual(second.Version, secondStored.Version);
        Assert.NotNull(error);
        return error;
    }
}
