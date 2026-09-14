using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Sidequest.Infrastructure.Persistence;

internal sealed class SqlConflictTransactionInterceptor : DbTransactionInterceptor
{
    internal static readonly SqlConflictTransactionInterceptor Instance = new();

    /// <inheritdoc />
    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        SqlServerFailures.ThrowIfConflict(eventData.Exception);

    /// <inheritdoc />
    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        SqlServerFailures.ThrowIfConflict(eventData.Exception);
        return Task.CompletedTask;
    }
}
