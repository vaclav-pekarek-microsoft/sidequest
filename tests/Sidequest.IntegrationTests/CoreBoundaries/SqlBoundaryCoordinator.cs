using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.CoreBoundaries;

internal static class SqlBoundaryCoordinator
{
    internal static SqlConnection Connection(SqlTestDatabase database)
    {
        using var template = database.CreateContext();
        return new SqlConnection(template.Database.GetConnectionString());
    }

    internal static async Task<int> SessionAsync(SqlConnection connection, SqlTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT @@SPID";
        command.CommandTimeout = 30;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    internal static async Task<int> ExecuteAsync(SqlConnection connection, SqlTransaction? transaction,
        string sql, Guid? id = null, CancellationToken token = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        if (id is not null)
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.UniqueIdentifier) { Value = id.Value });
        return await command.ExecuteNonQueryAsync(token);
    }

    internal static async Task WaitForBlockAsync(SqlTestDatabase database, int waiter, int holder,
        CancellationToken token)
    {
        await using var observer = Connection(database);
        await observer.OpenAsync(token);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
        {
            await using var command = observer.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sys.dm_exec_requests
                WHERE session_id = @waiter AND blocking_session_id = @holder AND wait_type LIKE 'LCK_M_%'
                """;
            command.Parameters.AddWithValue("@waiter", waiter);
            command.Parameters.AddWithValue("@holder", holder);
            command.CommandTimeout = 5;
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1)
                return;
            // The DMV relationship is the synchronization oracle; this delay only limits polling traffic.
            await Task.Delay(25, token);
        }
        throw new TimeoutException("The expected owned SQL lock wait was not observed within twenty seconds.");
    }

    internal static void AssertSafeConflict(Exception? actual, string message)
    {
        Assert.True(actual is Sidequest.Domain.Rules.DomainException,
            $"Expected safe DomainException; observed {actual?.GetType().Name ?? "no error"}" +
            (actual is SqlException sql ? $" with SQL number {sql.Number}." : "."));
        FoundationSeed.AssertConflict(actual, message);
        Assert.Null(actual!.InnerException);
        Assert.DoesNotContain("CB-PRIVATE-DETAIL", actual.ToString());
    }
}
