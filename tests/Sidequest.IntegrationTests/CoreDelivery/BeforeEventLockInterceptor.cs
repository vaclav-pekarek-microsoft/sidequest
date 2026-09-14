using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.IntegrationTests.CoreDelivery;

internal sealed class BeforeEventLockInterceptor(Func<CancellationToken, Task> beforeLock) : DbCommandInterceptor
{
    private int _invoked;

    /// <inheritdoc/>
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal) &&
            Interlocked.Exchange(ref _invoked, 1) == 0)
            await beforeLock(cancellationToken);
        return result;
    }
}
