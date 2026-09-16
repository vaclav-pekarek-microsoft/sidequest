using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.IntegrationTests.CoreBoundaries;

internal sealed class CrossResourceSaveGate : SaveChangesInterceptor
{
    internal TaskCompletionSource<int> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal IInterceptor Commands => new DeliveryReadProbe(this);

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Session.TrySetResult(((SqlConnection)eventData.Context!.Database.GetDbConnection()).ServerProcessId);
        Ready.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return result;
    }

    private sealed class DeliveryReadProbe(CrossResourceSaveGate gate) : DbCommandInterceptor
    {
        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
                (command.CommandText.Contains("[Notifications]", StringComparison.Ordinal) ||
                 command.CommandText.Contains("[CalendarDeliveryStates]", StringComparison.Ordinal)))
                gate.Session.TrySetResult(((SqlConnection)command.Connection!).ServerProcessId);
            return ValueTask.FromResult(result);
        }
    }
}
