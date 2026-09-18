using System.Data;
using Microsoft.Data.SqlClient;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Operations;
using Sidequest.Domain.Model;

namespace Sidequest.Infrastructure.Operations;

/// <summary>Reads three bounded SQL aggregate rows without loading entities, payloads, or altering leases.</summary>
/// <param name="factory">Creates a new SQL context owned and disposed by each sample.</param>
/// <param name="clock">Supplies the common UTC eligibility cutoff for all queues.</param>
/// <exception cref="ArgumentNullException">A required dependency is null.</exception>
/// <remarks>Use independently of application transactions. Aggregate reads are operational estimates, not a cross-queue transactional snapshot.</remarks>
public sealed class SqlOperationalQueueReader(ISidequestDbContextFactory factory, TimeProvider clock) : IOperationalQueueReader
{
    private readonly ISidequestDbContextFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc/>
    /// <remarks>
    /// SQL command timeout is five seconds. Due includes Pending DueUtc at/before the cutoff and
    /// Processing LeaseUntilUtc at/before the cutoff, excluding exhausted attempts just as SqlWorkQueue does.
    /// Oldest age uses DueUtc for Pending and lease expiry for abandoned Processing, never payload deadlines.
    /// </remarks>
    public async Task<IReadOnlyList<QueueObservation>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (OperationalSql.IsExpectedFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw OperationalSql.Translate(exception);
        }
    }

    private async Task<IReadOnlyList<QueueObservation>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var owned = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var db = OperationalSql.RequireSql(owned);
        var connection = await OperationalSql.OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = string.Join("\nUNION ALL\n",
            new[] { "OutboxMessages", "ScheduledWork", "NotificationDeliveries" }.Select(AggregateSql)) + "\nORDER BY [QueueOrdinal]";
        var now = clock.GetUtcNow();
        command.Parameters.Add(new SqlParameter("@now", now));
        command.Parameters.Add(new SqlParameter("@pending", SqlDbType.Int) { Value = (int)WorkStatus.Pending });
        command.Parameters.Add(new SqlParameter("@processing", SqlDbType.Int) { Value = (int)WorkStatus.Processing });
        command.Parameters.Add(new SqlParameter("@deadLetter", SqlDbType.Int) { Value = (int)WorkStatus.DeadLetter });
        var observations = new List<QueueObservation>(3);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var queue = (OperationalQueue)reader.GetInt32(0);
            observations.Add(new(queue, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.IsDBNull(4) ? null : now - reader.GetFieldValue<DateTimeOffset>(4)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (observations.Count != 3)
            throw new InvalidOperationException("Operational query did not return all queue aggregates.");
        return observations.AsReadOnly();
    }

    private static string AggregateSql(string table, int ordinal) => $"""
        SELECT {ordinal} AS [QueueOrdinal],
            COALESCE(SUM(CASE WHEN [Status] = @pending THEN CAST(1 AS bigint) ELSE 0 END), 0),
            COUNT_BIG([EligibleUtc]),
            COALESCE(SUM(CASE WHEN [Status] = @deadLetter THEN CAST(1 AS bigint) ELSE 0 END), 0),
            MIN([EligibleUtc])
        FROM (
            SELECT [Status], CASE WHEN [Attempts] < 8 THEN
                CASE WHEN [Status] = @pending AND [DueUtc] <= @now THEN [DueUtc]
                     WHEN [Status] = @processing AND [LeaseUntilUtc] <= @now THEN [LeaseUntilUtc] END
                END AS [EligibleUtc]
            FROM [{table}]
        ) AS [queue]
        """;
}
