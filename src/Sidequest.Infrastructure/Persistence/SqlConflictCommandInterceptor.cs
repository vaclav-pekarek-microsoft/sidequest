using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.Infrastructure.Persistence;

internal sealed class SqlConflictCommandInterceptor : DbCommandInterceptor
{
    internal static readonly SqlConflictCommandInterceptor Instance = new();

    /// <inheritdoc />
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result) =>
        new SqlConflictDataReader(result);

    /// <inheritdoc />
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<DbDataReader>(new SqlConflictDataReader(result));

    /// <inheritdoc />
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        SqlServerFailures.ThrowIfConflict(eventData.Exception);

    /// <inheritdoc />
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        SqlServerFailures.ThrowIfConflict(eventData.Exception);
        return Task.CompletedTask;
    }
}
