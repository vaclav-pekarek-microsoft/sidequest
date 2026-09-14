using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.IntegrationTests.CoreDelivery;

internal sealed class SqlCommandOrderObserver : DbCommandInterceptor
{
    internal ConcurrentQueue<(Guid TransactionId, string Sql)> Commands { get; } = new();

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Record(command, eventData);
        return ValueTask.FromResult(result);
    }

    private void Record(DbCommand command, CommandEventData eventData)
    {
        if (eventData.Context?.Database.CurrentTransaction is { } transaction)
            Commands.Enqueue((transaction.TransactionId, command.CommandText));
    }
}
