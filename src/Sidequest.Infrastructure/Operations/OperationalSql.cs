using System.Data;
using System.Data.Common;
using System.Transactions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Operations;

namespace Sidequest.Infrastructure.Operations;

internal static class OperationalSql
{
    internal static DbContext RequireSql(ISidequestDbContext context)
    {
        if (context is not DbContext db || !db.Database.IsSqlServer())
            throw new InvalidOperationException("Operational observation requires SQL Server.");
        if (Transaction.Current is not null || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Operational observation cannot join an application transaction.");
        return db;
    }

    internal static bool IsExpectedFailure(Exception exception) =>
        exception is DbException or InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException;

    internal static async Task<DbConnection> OpenConnectionAsync(DbContext db, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = db.Database.GetDbConnection();
        // Use the owned connection directly so EF's failed-command logging cannot attach raw provider exceptions.
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal static OperationalObservationException Translate(Exception exception) => new(exception switch
    {
        SqlException { Number: -2 } or TimeoutException or OperationCanceledException => OperationalFailureKind.Timeout,
        SqlException { Number: 208 or 207 } => OperationalFailureKind.SchemaUnavailable,
        DbException => OperationalFailureKind.StoreUnavailable,
        _ => OperationalFailureKind.Configuration
    });
}
