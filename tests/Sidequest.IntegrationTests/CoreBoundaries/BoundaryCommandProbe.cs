using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.IntegrationTests.CoreBoundaries;

internal sealed class BoundaryCommandProbe : DbCommandInterceptor
{
    private int calls;
    internal int Calls => Volatile.Read(ref calls);
    internal int Cancellations { get; private set; }

    /// <inheritdoc />
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) =>
        Cancellations++;

    /// <inheritdoc />
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Cancellations++;
        return Task.CompletedTask;
    }

    private void Count(DbCommand command)
    {
        if (command.CommandText.Contains("CB-VICTIM", StringComparison.Ordinal))
            Interlocked.Increment(ref calls);
    }

    /// <inheritdoc />
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Count(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Count(command);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Count(command);
        return ValueTask.FromResult(result);
    }
}
