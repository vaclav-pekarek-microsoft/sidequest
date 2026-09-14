using System.Data;
using System.Data.Common;

namespace Sidequest.IntegrationTests.CoreBoundaries;

internal sealed class ThrowingCompletionTransaction(DbTransaction inner, Exception failure) : DbTransaction
{
    internal int Calls { get; private set; }

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel => inner.IsolationLevel;

    /// <inheritdoc />
    protected override DbConnection? DbConnection => inner.Connection;

    /// <inheritdoc />
    public override void Commit()
    {
        Calls++;
        throw failure;
    }

    /// <inheritdoc />
    public override void Rollback()
    {
        Calls++;
        throw failure;
    }

    /// <inheritdoc />
    public override Task CommitAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromException(failure);
    }

    /// <inheritdoc />
    public override Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromException(failure);
    }
}
