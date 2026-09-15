using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure.Operations;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.ReleaseOperations;

/// <summary>Verifies generic anonymous readiness results and cancellation without contacting SQL or optional providers.</summary>
public sealed class SqlReadinessCheckTests
{
    /// <summary>The unchanged health registration resolves a scoped SQL factory while a process-only liveness selection performs no SQL.</summary>
    /// <returns>A task completing after independent liveness and generic readiness reports are verified.</returns>
    [Fact]
    public async Task ReadinessRegistrationUsesScopedFactoryAndLeavesLivenessIndependent()
    {
        var factory = new FailingContextFactory(new InvalidOperationException("secret must not escape"));
        var services = new ServiceCollection().AddLogging()
            .AddScoped<ISidequestDbContextFactory>(_ => factory);
        services.AddSidequestOperationalPersistence();
        services.AddHealthChecks().AddCheck<SqlReadinessCheck>("sql", tags: ["ready"]);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var checks = provider.GetRequiredService<HealthCheckService>();
        var live = await checks.CheckHealthAsync(_ => false);
        Assert.Equal(HealthStatus.Healthy, live.Status);
        Assert.Empty(live.Entries);
        Assert.Equal(0, factory.Calls);
        var ready = await checks.CheckHealthAsync(check => check.Tags.Contains("ready"));
        Assert.Equal(HealthStatus.Unhealthy, ready.Status);
        var sql = Assert.Single(ready.Entries);
        Assert.Equal("sql", sql.Key);
        Assert.Null(sql.Value.Exception);
        Assert.Null(sql.Value.Description);
        Assert.Empty(sql.Value.Data);
        Assert.Equal(1, factory.Calls);
    }

    /// <summary>Expected configuration and timeout failures expose neither exception details nor secret-bearing health data or log records.</summary>
    /// <param name="timeout">Whether to simulate timeout instead of invalid configuration.</param>
    /// <returns>A task completing after exact safe-surface assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedReadinessFailuresAreGenericAndRedacted(bool timeout)
    {
        using var logs = new OperationalLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var factory = new FailingContextFactory(timeout
            ? new TimeoutException("secret endpoint payload")
            : new InvalidOperationException("secret connection string"));
        var check = new SqlReadinessCheck(new SqlOperationalReadinessProbe(factory), loggerFactory.CreateLogger<SqlReadinessCheck>());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Description);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
        Assert.Equal(1, factory.Calls);
        var log = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal($"SQL readiness unavailable: {(timeout ? "timeout" : "configuration")}.", log.Message);
        Assert.Null(log.Exception);
    }

    /// <summary>Pre-canceled readiness and queue sampling never invoke context creation or publish failures.</summary>
    /// <returns>A task completing after both cancellation contracts are verified.</returns>
    [Fact]
    public async Task ReadinessAndSamplerCancellationPropagateWithoutDatabaseAccess()
    {
        using var logs = new OperationalLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var factory = new FailingContextFactory(new InvalidOperationException("must not be called"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var check = new SqlReadinessCheck(new SqlOperationalReadinessProbe(factory), loggerFactory.CreateLogger<SqlReadinessCheck>());
        var readiness = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => check.CheckHealthAsync(new HealthCheckContext(), canceled.Token));
        var sampling = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SqlQueueSampler(new SqlOperationalQueueReader(factory, TimeProvider.System)).SampleAsync(canceled.Token));
        Assert.Equal(canceled.Token, readiness.CancellationToken);
        Assert.Equal(canceled.Token, sampling.CancellationToken);
        Assert.Equal(0, factory.Calls);
        Assert.Empty(logs.Entries);
    }

    /// <summary>Public operational dependencies reject null values during construction rather than failing during background work.</summary>
    [Fact]
    public void NullProbeAndSamplerDependenciesAreRejected()
    {
        using var factory = LoggerFactory.Create(_ => { });
        var logger = factory.CreateLogger<SqlReadinessCheck>();
        var contextFactory = new FailingContextFactory(new InvalidOperationException());
        Assert.Throws<ArgumentNullException>(() => new SqlReadinessCheck(null!, logger));
        Assert.Throws<ArgumentNullException>(() => new SqlReadinessCheck(new SqlOperationalReadinessProbe(contextFactory), null!));
        Assert.Throws<ArgumentNullException>(() => new SqlQueueSampler(null!));
        Assert.Throws<ArgumentNullException>(() => new SqlOperationalReadinessProbe(null!));
        Assert.Throws<ArgumentNullException>(() => new SqlOperationalQueueReader(null!, TimeProvider.System));
        Assert.Throws<ArgumentNullException>(() => new SqlOperationalQueueReader(contextFactory, null!));
    }
}
