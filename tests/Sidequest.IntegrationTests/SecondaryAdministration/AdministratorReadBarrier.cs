using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.IntegrationTests.SecondaryAdministration;

internal sealed class AdministratorReadBarrier : DbCommandInterceptor
{
    private readonly TaskCompletionSource bothReads = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int readers;

    /// <inheritdoc />
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("[Administrators]", StringComparison.Ordinal) &&
            command.CommandText.Contains("INNER JOIN [Users]", StringComparison.Ordinal))
        {
            if (Interlocked.Increment(ref readers) == 2)
                bothReads.SetResult();
            await bothReads.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        return result;
    }
}
