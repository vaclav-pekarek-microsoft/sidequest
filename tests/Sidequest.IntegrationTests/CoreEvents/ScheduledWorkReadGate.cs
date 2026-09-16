using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.IntegrationTests.CoreEvents;

internal sealed class ScheduledWorkReadGate : DbCommandInterceptor
{
    internal TaskCompletionSource<int> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (IsScheduleRead(command))
            Started.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (IsScheduleRead(command))
        {
            Read.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }

    private static bool IsScheduleRead(DbCommand command) =>
        command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
        command.CommandText.Contains("FROM [ScheduledWork]", StringComparison.Ordinal) &&
        command.CommandText.Contains("[DeduplicationKey]", StringComparison.Ordinal);
}
