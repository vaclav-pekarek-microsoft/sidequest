using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Operations;

namespace Sidequest.Infrastructure.Operations;

/// <summary>Checks SQL connectivity and exact migration-history compatibility without creating or changing the database.</summary>
/// <param name="factory">Creates a fresh SQL context whose connection and readers are owned by each probe.</param>
/// <exception cref="ArgumentNullException">The context factory is null.</exception>
/// <remarks>Uses the existing default migration-history table and a nonempty compiled migration set; this is not a full audit of manual schema drift.</remarks>
public sealed class SqlOperationalReadinessProbe(ISidequestDbContextFactory factory) : IOperationalReadinessProbe
{
    private readonly ISidequestDbContextFactory factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <inheritdoc/>
    /// <remarks>Commands time out after five seconds; connection opening honors deployment connection settings and cancellation. Expected provider failures leave this boundary only as sanitized typed failures.</remarks>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var owned = await factory.CreateAsync(cancellationToken).ConfigureAwait(false);
            var db = OperationalSql.RequireSql(owned);
            var expected = db.Database.GetMigrations().ToArray();
            var connection = await OperationalSql.OpenConnectionAsync(db, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "SELECT [MigrationId] FROM [__EFMigrationsHistory] ORDER BY [MigrationId]";
            var applied = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                applied.Add(reader.GetString(0));
            cancellationToken.ThrowIfCancellationRequested();
            if (expected.Length == 0 || !expected.SequenceEqual(applied, StringComparer.Ordinal))
                throw new OperationalObservationException(OperationalFailureKind.SchemaMismatch);
        }
        catch (Exception exception) when (OperationalSql.IsExpectedFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw OperationalSql.Translate(exception);
        }
    }
}
