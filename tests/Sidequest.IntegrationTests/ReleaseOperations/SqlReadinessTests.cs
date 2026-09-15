using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Operations;
using Sidequest.Infrastructure.Operations;
using Sidequest.Infrastructure.Persistence;
using Sidequest.Web.Operations;

namespace Sidequest.IntegrationTests.ReleaseOperations;

/// <summary>Proves readiness against actual SQL migration history in exclusively owned fixture catalogs, never the development database.</summary>
public sealed class SqlReadinessTests
{
    /// <summary>A fully migrated catalog is healthy with status-only data and no tracking or saves, including repeated probes.</summary>
    /// <returns>A task completing after history and context-disposal assertions.</returns>
    [Fact]
    public async Task CurrentSchemaIsHealthy()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await using var read = scenario.Database.CreateContext();
        var before = (await read.Database.GetAppliedMigrationsAsync()).ToArray();
        var check = new SqlReadinessCheck(new SqlOperationalReadinessProbe(scenario), NullLogger<SqlReadinessCheck>.Instance);
        AssertSafe(await check.CheckHealthAsync(new()), HealthStatus.Healthy);
        AssertSafe(await check.CheckHealthAsync(new()), HealthStatus.Healthy);
        Assert.Equal(before, await read.Database.GetAppliedMigrationsAsync());
        Assert.Equal(2, scenario.Contexts.Count);
        Assert.Equal(0, scenario.TrackedEntities);
        Assert.Equal(0, scenario.Saves);
        foreach (var context in scenario.Contexts)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Users.AnyAsync());
    }

    /// <summary>An empty catalog remains unready and unchanged; checking migration readiness never creates schema objects.</summary>
    /// <returns>A task completing after the empty-schema and absent migration evidence is rechecked.</returns>
    [Fact]
    public async Task MissingSchemaIsUnhealthyWithoutCreatingObjects()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await using var setup = scenario.Database.CreateContext();
        await setup.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        await setup.Database.ExecuteSqlRawAsync("DROP TABLE [__EFMigrationsHistory]");
        IOperationalReadinessProbe probe = new SqlOperationalReadinessProbe(scenario);
        var failure = await Assert.ThrowsAsync<OperationalObservationException>(() => probe.ProbeAsync());
        Assert.Equal(OperationalFailureKind.SchemaUnavailable, failure.Kind);
        Assert.Equal("Operational observation is unavailable.", failure.Message);
        Assert.Null(failure.InnerException);
        var check = new SqlReadinessCheck(probe, NullLogger<SqlReadinessCheck>.Instance);
        AssertSafe(await check.CheckHealthAsync(new()), HealthStatus.Unhealthy);
        Assert.Equal(0, await setup.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables").SingleAsync());
        Assert.Empty(await setup.Database.GetAppliedMigrationsAsync());
        Assert.Equal(0, scenario.Saves);
    }

    /// <summary>Existing application tables are insufficient when the compiled migration is missing from applied history.</summary>
    /// <returns>A task completing after a pending migration is rejected without silently stamping it applied.</returns>
    [Fact]
    public async Task PendingMigrationIsUnhealthy()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await using var setup = scenario.Database.CreateContext();
        var migration = Assert.Single(setup.Database.GetMigrations());
        await setup.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] = {migration}");
        IOperationalReadinessProbe probe = new SqlOperationalReadinessProbe(scenario);
        var failure = await Assert.ThrowsAsync<OperationalObservationException>(() => probe.ProbeAsync());
        Assert.Equal(OperationalFailureKind.SchemaMismatch, failure.Kind);
        Assert.Null(failure.InnerException);
        var check = new SqlReadinessCheck(probe, NullLogger<SqlReadinessCheck>.Instance);
        AssertSafe(await check.CheckHealthAsync(new()), HealthStatus.Unhealthy);
        Assert.Equal(new[] { migration }, await setup.Database.GetPendingMigrationsAsync());
        Assert.Empty(await setup.OutboxMessages.ToListAsync());
        Assert.Equal(0, scenario.Saves);
    }

    /// <summary>An accidentally unconfigured migration assembly cannot report ready merely because both migration lists are empty.</summary>
    /// <returns>A task completing after the empty compiled/history sets are proven unready.</returns>
    [Fact]
    public async Task MissingCompiledMigrationsCannotReportEmptyHistoryReady()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await using var setup = scenario.Database.CreateContext();
        await setup.Database.ExecuteSqlRawAsync("DELETE FROM [__EFMigrationsHistory]");
        var options = new DbContextOptionsBuilder<SidequestDbContext>()
            .UseSqlServer(setup.Database.GetConnectionString(),
                sql => sql.MigrationsAssembly(typeof(SqlReadinessTests).Assembly.FullName)).Options;
        await using (var empty = new SidequestDbContext(options))
            Assert.Empty(empty.Database.GetMigrations());
        var check = new SqlReadinessCheck(new SqlOperationalReadinessProbe(new OptionsFactory(options)), NullLogger<SqlReadinessCheck>.Instance);
        AssertSafe(await check.CheckHealthAsync(new()), HealthStatus.Unhealthy);
        Assert.Empty(await setup.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>A database migrated beyond this application's compiled schema fails closed instead of claiming backward compatibility.</summary>
    /// <returns>A task completing after unknown history is left untouched and readiness remains generic.</returns>
    [Fact]
    public async Task UnknownMigrationIsUnhealthy()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await using var setup = scenario.Database.CreateContext();
        await setup.Database.ExecuteSqlRawAsync(
            "INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES ('99999999999999_Future', '10.0.12')");
        AssertSafe(await new SqlReadinessCheck(new SqlOperationalReadinessProbe(scenario), NullLogger<SqlReadinessCheck>.Instance).CheckHealthAsync(new()), HealthStatus.Unhealthy);
        Assert.Equal(2, (await setup.Database.GetAppliedMigrationsAsync()).Count());
    }

    /// <summary>Readiness succeeds using a SELECT-only principal and does not depend on worker or migration permissions.</summary>
    /// <returns>A task completing after the read-only principal probes a current catalog.</returns>
    [Fact]
    public async Task CurrentSchemaReadinessNeedsOnlyReadPermissions()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await using (var setup = scenario.Database.CreateContext())
            await setup.Database.ExecuteSqlRawAsync("CREATE USER [OperationalReadOnly] WITHOUT LOGIN; GRANT SELECT TO [OperationalReadOnly];");
        scenario.ReadOnlyUser = true;
        AssertSafe(await new SqlReadinessCheck(new SqlOperationalReadinessProbe(scenario), NullLogger<SqlReadinessCheck>.Instance).CheckHealthAsync(new()), HealthStatus.Healthy);
        Assert.Equal(0, scenario.Saves);
    }

    /// <summary>Cancellation after an owned SQL connection opens propagates through both probes and disposes their contexts without changing schema.</summary>
    /// <returns>A task completing after exact token, ownership and unchanged history assertions.</returns>
    [Fact]
    public async Task CancellationAfterContextAcquisitionDisposesBothProbes()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        using var readinessCancellation = new CancellationTokenSource();
        var readinessFactory = new CancelingFactory(scenario, readinessCancellation);
        var readiness = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqlReadinessCheck(new SqlOperationalReadinessProbe(readinessFactory), NullLogger<SqlReadinessCheck>.Instance)
                .CheckHealthAsync(new(), readinessCancellation.Token));
        Assert.Equal(readinessCancellation.Token, readiness.CancellationToken);
        using var sampleCancellation = new CancellationTokenSource();
        var sampleFactory = new CancelingFactory(scenario, sampleCancellation);
        var sampling = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqlQueueSampler(new SqlOperationalQueueReader(sampleFactory, scenario.Clock)).SampleAsync(sampleCancellation.Token));
        Assert.Equal(sampleCancellation.Token, sampling.CancellationToken);
        Assert.Equal(2, scenario.Contexts.Count);
        foreach (var context in scenario.Contexts)
            await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Users.AnyAsync());
        Assert.Equal(0, scenario.TrackedEntities);
        Assert.Equal(0, scenario.Saves);
        await using var read = scenario.Database.CreateContext();
        Assert.Equal(read.Database.GetMigrations(), await read.Database.GetAppliedMigrationsAsync());
    }

    private static void AssertSafe(HealthCheckResult result, HealthStatus expected)
    {
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
    }

    private sealed class CancelingFactory(OperationalSqlScenario scenario, CancellationTokenSource cancellation) : ISidequestDbContextFactory
    {
        /// <inheritdoc/>
        public async Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
        {
            var context = await scenario.CreateAsync(cancellationToken);
            try
            {
                await ((DbContext)context).Database.OpenConnectionAsync(cancellationToken);
                cancellation.Cancel();
                return context;
            }
            catch
            {
                await context.DisposeAsync();
                throw;
            }
        }

    }

    private sealed class OptionsFactory(DbContextOptions<SidequestDbContext> options) : ISidequestDbContextFactory
    {
        /// <inheritdoc/>
        public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ISidequestDbContext>(new SidequestDbContext(options));
        }
    }
}
