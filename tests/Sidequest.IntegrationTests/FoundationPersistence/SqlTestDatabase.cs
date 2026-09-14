using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Sidequest.Infrastructure.Persistence;

namespace Sidequest.IntegrationTests.FoundationPersistence;

/// <summary>Owns one uniquely named SQL test database, applies real migrations, and drops only its proven-owned catalog.</summary>
/// <remarks>Uses SIDEQUEST_TEST_SQL when configured, otherwise Windows LocalDB integrated authentication; never skips or falls back after failure.</remarks>
/// <remarks>Initialization and disposal must not overlap other fixture operations. Separate contexts may be created after initialization, but each DbContext belongs to one sequential operation and must be disposed before catalog cleanup.</remarks>
public sealed class SqlTestDatabase : IAsyncLifetime
{
    private readonly string name = $"SidequestTests_{Guid.NewGuid():N}";
    private string connectionString = "";
    private string managementConnectionString = "";
    private bool created;

    /// <summary>Creates an independent production SQL context targeting this initialized fixture's catalog.</summary>
    /// <returns>A context that the caller must dispose before fixture cleanup.</returns>
    /// <example>
    /// <code>
    /// var fixture = new SqlTestDatabase();
    /// try
    /// {
    ///     await fixture.InitializeAsync();
    ///     await using var context = fixture.CreateContext();
    ///     Assert.Empty(await context.Users.ToListAsync());
    /// }
    /// finally
    /// {
    ///     await fixture.DisposeAsync();
    /// }
    /// </code>
    /// </example>
    public SidequestDbContext CreateContext() => new(
        new DbContextOptionsBuilder<SidequestDbContext>().UseSqlServer(connectionString).Options);

    /// <inheritdoc/>
    /// <remarks>Creates a GUID-named catalog and runs Database.MigrateAsync; unsuccessful initialization cleans up only after creation ownership is established.</remarks>
    /// <exception cref="InvalidOperationException">Configuration, SQL availability, migration, or owned-database cleanup fails; diagnostics omit raw credentials.</exception>
    public async Task InitializeAsync()
    {
        var stage = "connection configuration";
        var initialized = false;
        try
        {
            var configured = Environment.GetEnvironmentVariable("SIDEQUEST_TEST_SQL");
            var builder = new SqlConnectionStringBuilder(configured ??
                @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true");
            // No caller-supplied catalog is ever owned. Disable only this fixture's pooling:
            // disposal then closes physical connections without touching any unrelated pool.
            builder.InitialCatalog = name;
            builder.AttachDBFilename = "";
            builder.Pooling = false;
            connectionString = builder.ConnectionString;
            builder.InitialCatalog = "master";
            managementConnectionString = builder.ConnectionString;
            stage = "unique database creation";
            await using (var master = new SqlConnection(managementConnectionString))
            {
                await master.OpenAsync();
                await using var command = master.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{name}]";
                await command.ExecuteNonQueryAsync();
                created = true;
            }
            stage = "Database.MigrateAsync";
            await using var db = CreateContext();
            await db.Database.MigrateAsync();
            initialized = true;
        }
        catch (SqlException error)
        {
            throw InitializationFailure(stage, error);
        }
        catch (ArgumentException error)
        {
            throw InitializationFailure(stage, error);
        }
        catch (InvalidOperationException error)
        {
            throw InitializationFailure(stage, error);
        }
        finally
        {
            // Also clean up if an unexpected migration exception propagates. No broad catch
            // can turn it into success, and no database is dropped before ownership is proven.
            if (!initialized && created)
                await DisposeAsync();
        }
    }

    // Do not attach raw provider exceptions: they can contain endpoints or credentials.
    private InvalidOperationException InitializationFailure(string stage, Exception error) => new(
                $"Real SQL foundation fixture failed during {stage} for owned database {name} " +
                $"using {(Environment.GetEnvironmentVariable("SIDEQUEST_TEST_SQL") is null ? "LocalDB integrated authentication" : "SIDEQUEST_TEST_SQL server")}: " +
                $"{SafeError(error)}. Check SQL availability, CREATE DATABASE permissions and migration compatibility. No fallback/skip.");

    /// <inheritdoc/>
    /// <remarks>Terminates connections only to this fixture's validated catalog and drops it; repeated disposal after success does nothing.</remarks>
    /// <exception cref="InvalidOperationException">The owned name is invalid or SQL cleanup fails.</exception>
    public async Task DisposeAsync()
    {
        if (!created)
            return;
        if (!name.StartsWith("SidequestTests_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name["SidequestTests_".Length..], "N", out _))
            throw new InvalidOperationException("Refusing cleanup of an unvalidated database name.");
        try
        {
            await using var master = new SqlConnection(managementConnectionString);
            await master.OpenAsync();
            await using var command = master.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];";
            await command.ExecuteNonQueryAsync();
            created = false;
        }
        catch (SqlException error)
        {
            throw new InvalidOperationException($"Cleanup of owned database {name} failed ({SafeError(error)}).");
        }
    }

    private static string SafeError(Exception error) =>
        error is SqlException sql ? $"SqlException number {sql.Number}" : error.GetType().Name;
}
