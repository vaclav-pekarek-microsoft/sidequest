using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Operations;
using Sidequest.Infrastructure.Operations;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.ReleaseOperations;

/// <summary>Verifies the provider-neutral operational boundary, sanitized adapter failures and always-on readiness composition.</summary>
public sealed class OperationalBoundaryTests
{
    /// <summary>Both SQL adapters replace expected raw failures with the same closed kind, fixed safe message and no inner exception.</summary>
    /// <param name="failureCase">Selects invalid configuration, database failure, timeout, uncoupled cancellation or invalid argument.</param>
    /// <param name="expected">The provider-neutral failure classification expected at both ports.</param>
    /// <returns>A task completing after safe typed failures are inspected for both adapters.</returns>
    [Theory]
    [InlineData(0, OperationalFailureKind.Configuration)]
    [InlineData(1, OperationalFailureKind.StoreUnavailable)]
    [InlineData(2, OperationalFailureKind.Timeout)]
    [InlineData(3, OperationalFailureKind.Timeout)]
    [InlineData(4, OperationalFailureKind.Configuration)]
    public async Task SqlAdaptersDoNotExposeRawProviderFailures(int failureCase, OperationalFailureKind expected)
    {
        Exception raw = failureCase switch
        {
            0 => new InvalidOperationException("secret connection configuration"),
            1 => new PrivateDatabaseFailure(),
            2 => new TimeoutException("secret server timeout"),
            3 => new OperationCanceledException("secret provider cancellation"),
            _ => new ArgumentException("secret connection argument")
        };
        var factory = new FailingContextFactory(raw);
        IOperationalReadinessProbe probe = new SqlOperationalReadinessProbe(factory);
        IOperationalQueueReader reader = new SqlOperationalQueueReader(factory, TimeProvider.System);
        AssertSafe(await Assert.ThrowsAsync<OperationalObservationException>(() => probe.ProbeAsync()), expected);
        AssertSafe(await Assert.ThrowsAsync<OperationalObservationException>(() => reader.ReadAsync()), expected);
        Assert.Equal(2, factory.Calls);
    }

    /// <summary>Registration is I/O-free, preserves the clock, scopes both adapters and keeps readiness wired when monitoring is disabled.</summary>
    /// <returns>A task completing after an independently resolved readiness report is verified without enabling a sampler.</returns>
    [Fact]
    public async Task PersistenceRegistrationIsRequiredIndependentlyOfMonitoring()
    {
        var factory = new FailingContextFactory(new PrivateDatabaseFailure());
        var clock = new OperationalTestClock();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(clock)
            .AddScoped<ISidequestDbContextFactory>(_ => factory);
        Assert.Same(services, services.AddSidequestOperationalPersistence());
        services.AddSidequestOperationalMonitoring(new ConfigurationBuilder().Build());
        services.AddHealthChecks().AddCheck<SqlReadinessCheck>("sql", tags: ["ready"]);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        Assert.Equal(0, factory.Calls);
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        Assert.False(provider.GetRequiredService<OperationalMonitoringOptions>().Enabled);
        Assert.Null(provider.GetService<OperationalQueueMetrics>());
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var probe = first.ServiceProvider.GetRequiredService<IOperationalReadinessProbe>();
        var reader = first.ServiceProvider.GetRequiredService<IOperationalQueueReader>();
        Assert.IsType<SqlOperationalReadinessProbe>(probe);
        Assert.IsType<SqlOperationalQueueReader>(reader);
        Assert.Same(probe, first.ServiceProvider.GetRequiredService<IOperationalReadinessProbe>());
        Assert.Same(reader, first.ServiceProvider.GetRequiredService<IOperationalQueueReader>());
        Assert.NotSame(probe, second.ServiceProvider.GetRequiredService<IOperationalReadinessProbe>());
        Assert.NotSame(reader, second.ServiceProvider.GetRequiredService<IOperationalQueueReader>());
        Assert.Equal(0, factory.Calls);
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(check => check.Tags.Contains("ready"));
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Null(Assert.Single(report.Entries).Value.Exception);
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>Default operational persistence composition supplies the system clock and activates no background service; invalid inputs cannot partially register it.</summary>
    [Fact]
    public void PersistenceRegistrationDefaultsAndNullValidation()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(() =>
            OperationalPersistenceRegistration.AddSidequestOperationalPersistence(null!)).ParamName);
        var services = new ServiceCollection();
        services.AddSidequestOperationalPersistence();
        using var provider = services.BuildServiceProvider();
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.Empty(provider.GetServices<IHostedService>());
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperationalObservationException((OperationalFailureKind)99));
    }

    /// <summary>A Web sampler uses only its reader port and preserves complete nonzero and actual-empty observations without guessing database behavior.</summary>
    /// <param name="empty">Whether the port reports successful empty queues instead of nonzero backlog.</param>
    /// <returns>A task completing after exact values and forwarded cancellation are asserted.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebSamplerConsumesProviderNeutralObservations(bool empty)
    {
        var rows = Enum.GetValues<OperationalQueue>()
            .Select(queue => new QueueObservation(queue, empty ? 0 : 4, empty ? 0 : 2, empty ? 0 : 1,
                empty ? null : TimeSpan.FromSeconds(7))).ToArray();
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader(token =>
        {
            Assert.Equal(cancellation.Token, token);
            return Task.FromResult<IReadOnlyList<QueueObservation>>(rows);
        });
        var result = await new SqlQueueSampler(reader).SampleAsync(cancellation.Token);
        Assert.Equal(Enum.GetValues<OperationalQueue>(), result.Select(row => row.Queue));
        Assert.All(result, row =>
        {
            Assert.Equal(empty ? 0 : 4, row.Pending);
            Assert.Equal(empty ? 0 : 2, row.Due);
            Assert.Equal(empty ? 0 : 1, row.DeadLetter);
            Assert.Equal(empty ? (TimeSpan?)null : TimeSpan.FromSeconds(7), row.OldestDueAge);
        });
        Assert.Equal(1, reader.Calls);
    }

    /// <summary>A typed unavailable result remains failure, while caller cancellation rejects a late successful port result and prevents subsequent reads.</summary>
    /// <returns>A task completing after failure identity, cancellation token and call-count assertions.</returns>
    [Fact]
    public async Task WebSamplerPreservesUnavailableAndRejectsLateCanceledResults()
    {
        var unavailable = new OperationalObservationException(OperationalFailureKind.StoreUnavailable);
        var failed = new Reader(_ => Task.FromException<IReadOnlyList<QueueObservation>>(unavailable));
        Assert.Same(unavailable, await Assert.ThrowsAsync<OperationalObservationException>(() => new SqlQueueSampler(failed).SampleAsync()));
        Assert.Equal(1, failed.Calls);
        using var cancellation = new CancellationTokenSource();
        var late = new Reader(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<QueueObservation>>(
                Enum.GetValues<OperationalQueue>().Select(queue => new QueueObservation(queue, 0, 0, 0, null)).ToArray());
        });
        var sampler = new SqlQueueSampler(late);
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.SampleAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.SampleAsync(cancellation.Token));
        Assert.Equal(1, late.Calls);
    }

    /// <summary>Provider-neutral failures retain their fixed operational log category and generic health surface without a SQL implementation in the Web adapter.</summary>
    /// <param name="kind">The closed failure kind from the application port.</param>
    /// <param name="classification">The existing log contract retained after moving provider classification out of Web.</param>
    /// <returns>A task completing after the health result and exact safe log entry are inspected.</returns>
    [Theory]
    [InlineData(OperationalFailureKind.SchemaMismatch, "schema-mismatch")]
    [InlineData(OperationalFailureKind.SchemaUnavailable, "schema-unavailable")]
    [InlineData(OperationalFailureKind.StoreUnavailable, "sql-unavailable")]
    [InlineData(OperationalFailureKind.Timeout, "timeout")]
    [InlineData(OperationalFailureKind.Configuration, "configuration")]
    public async Task WebReadinessConsumesOnlySafePortFailures(OperationalFailureKind kind, string classification)
    {
        using var logs = new OperationalLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var check = new SqlReadinessCheck(new UnavailableProbe(kind), loggerFactory.CreateLogger<SqlReadinessCheck>());
        var result = await check.CheckHealthAsync(new());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
        var log = Assert.Single(logs.Entries);
        Assert.Equal($"SQL readiness unavailable: {classification}.", log.Message);
        Assert.Null(log.Exception);
    }

    private static void AssertSafe(OperationalObservationException failure, OperationalFailureKind expected)
    {
        Assert.Equal(expected, failure.Kind);
        Assert.Equal("Operational observation is unavailable.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Empty(failure.Data);
    }

    private sealed class PrivateDatabaseFailure() : DbException("secret SQL server and credentials");

    private sealed class Reader(Func<CancellationToken, Task<IReadOnlyList<QueueObservation>>> read) : IOperationalQueueReader
    {
        internal int Calls { get; private set; }
        /// <inheritdoc/>
        public Task<IReadOnlyList<QueueObservation>> ReadAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return read(cancellationToken);
        }
    }

    private sealed class UnavailableProbe(OperationalFailureKind kind) : IOperationalReadinessProbe
    {
        /// <inheritdoc/>
        public Task ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new OperationalObservationException(kind));
    }
}
