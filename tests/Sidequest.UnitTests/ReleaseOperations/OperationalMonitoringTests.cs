using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure.Operations;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.ReleaseOperations;

/// <summary>Verifies opt-in composition, bounded scheduling, safe failures and awaited scoped shutdown without SQL or providers.</summary>
[Collection("Release operational metrics")]
public sealed class OperationalMonitoringTests
{
    /// <summary>Default hosts report disabled monitoring without resolving a factory, creating a meter or requiring a SQL dependency.</summary>
    /// <returns>A task completing after the disabled worker logs and stops.</returns>
    [Fact]
    public async Task DisabledHostDoesNotResolveDatabase()
    {
        using var capture = new MetricCapture();
        using var logs = new OperationalLogs();
        var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logs));
        Assert.Same(services, services.AddSidequestOperationalMonitoring(Configuration()));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = provider.GetRequiredService<OperationalMonitoringOptions>();
        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), options.SampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), options.StaleAfter);
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(default);
        await logs.Logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(default);
        Assert.Empty(capture.Read());
        Assert.Null(provider.GetService<SqlQueueSampler>());
        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Operational SQL monitoring disabled; no queue observations will be collected.", entry.Message);
        Assert.Null(entry.Exception);
    }

    /// <summary>Registration preserves the supplied clock and accepts both inclusive interval endpoints while validating scoped dependencies.</summary>
    /// <param name="seconds">The supported lower or upper interval in seconds.</param>
    [Theory]
    [InlineData(5)]
    [InlineData(300)]
    public void BoundsAndClockArePreserved(int seconds)
    {
        var clock = new OperationalTestClock();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(clock)
            .AddScoped<ISidequestDbContextFactory>(_ => new FailingContextFactory(new InvalidOperationException()));
        services.AddSidequestOperationalPersistence();
        services.AddSidequestOperationalMonitoring(Configuration("true", TimeSpan.FromSeconds(seconds).ToString()));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = provider.GetRequiredService<OperationalMonitoringOptions>();
        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(seconds), options.SampleInterval);
        Assert.Equal(TimeSpan.FromSeconds(seconds * 2), options.StaleAfter);
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<SqlQueueSampler>(), first.ServiceProvider.GetRequiredService<SqlQueueSampler>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<SqlQueueSampler>(), second.ServiceProvider.GetRequiredService<SqlQueueSampler>());
    }

    /// <summary>Invalid intervals or opt-in values fail before partially registering services, and parsing errors do not retain raw values.</summary>
    /// <param name="enabled">The configured opt-in value.</param>
    /// <param name="interval">The configured invalid or boundary-adjacent interval.</param>
    [Theory]
    [InlineData("false", "00:00:04.9999999")]
    [InlineData("true", "00:05:00.0000001")]
    [InlineData("true", "00:00:00")]
    [InlineData("true", "-00:00:01")]
    [InlineData("true", "secret-do-not-log")]
    [InlineData("secret-do-not-log", "00:00:30")]
    public void BoundsValidateBeforeRegistration(string enabled, string interval)
    {
        var services = new ServiceCollection();
        var failure = Record.Exception(() => services.AddSidequestOperationalMonitoring(Configuration(enabled, interval)));
        if (enabled == "secret-do-not-log" || interval == "secret-do-not-log")
        {
            var invalid = Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal("Operational monitoring settings could not be parsed.", invalid.Message);
            Assert.Null(invalid.InnerException);
        }
        else
            Assert.IsType<ArgumentOutOfRangeException>(failure);
        Assert.Empty(services);
    }

    /// <summary>Null host arguments fail deterministically before service collection mutation.</summary>
    [Fact]
    public void NullRegistrationInputsAreRejected()
    {
        Assert.Equal("services", Assert.Throws<ArgumentNullException>(
            () => OperationalMonitoringRegistration.AddSidequestOperationalMonitoring(null!, Configuration())).ParamName);
        var services = new ServiceCollection();
        Assert.Equal("configuration", Assert.Throws<ArgumentNullException>(
            () => services.AddSidequestOperationalMonitoring(null!)).ParamName);
        Assert.Empty(services);
    }

    /// <summary>Failed SQL attempts remain unavailable, run sequentially in new disposed scopes and use the configured cancellable delay.</summary>
    /// <returns>A task completing after two controlled attempts and timer cancellation.</returns>
    [Fact]
    public async Task FailedSamplingDoesNotPublishZerosAndRepeatsInFreshScopes()
    {
        using var capture = new MetricCapture();
        using var logs = new OperationalLogs();
        var clock = new OperationalTestClock();
        var factories = new List<ObservedFactory>();
        var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logs))
            .AddSingleton<TimeProvider>(clock)
            .AddScoped<ISidequestDbContextFactory>(_ =>
            {
                var factory = new ObservedFactory(block: false);
                factories.Add(factory);
                return factory;
            });
        services.AddSidequestOperationalPersistence();
        services.AddSidequestOperationalMonitoring(Configuration("true", "00:00:05"));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(default);
        var firstTimer = await clock.NextTimerAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), firstTimer.Due);
        var first = Assert.Single(factories);
        Assert.True(first.Disposed);
        Assert.Equal(1, first.Calls);
        var failed = capture.Read();
        Assert.Equal(6, failed.Count);
        Assert.Equal(0, failed[("sidequest.queue.observation_available", "outbox")]);
        Assert.Equal(1, failed[("sidequest.queue.observation_stale", "outbox")]);
        clock.Advance(firstTimer.Due);
        firstTimer.Fire();
        var secondTimer = await clock.NextTimerAsync();
        Assert.Equal(2, factories.Count);
        Assert.All(factories, factory => { Assert.True(factory.Disposed); Assert.Equal(1, factory.Calls); });
        Assert.Equal(6, capture.Read().Count);
        await worker.StopAsync(default);
        Assert.True(secondTimer.IsDisposed);
        Assert.Equal(2, logs.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.All(logs.Entries, entry =>
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain("secret", entry.Message);
        });
        Assert.All(logs.Entries.Where(entry => entry.Level == LogLevel.Warning), entry =>
            Assert.Equal("Operational SQL observation unavailable: configuration.", entry.Message));
    }

    /// <summary>All host shutdown paths cancel an active factory call and await scope cleanup before removing the meter.</summary>
    /// <param name="shutdownMode">Zero asynchronously disposes, one stops first, and two synchronously disposes the active host.</param>
    /// <returns>A task completing after cancellation, cleanup and meter assertions.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EnabledHostCancelsAndDisposesInFlightSample(int shutdownMode)
    {
        using var capture = new MetricCapture();
        var factory = new ObservedFactory(block: true);
        var services = new ServiceCollection().AddLogging()
            .AddScoped<ISidequestDbContextFactory>(_ => factory);
        services.AddSidequestOperationalPersistence();
        services.AddSidequestOperationalMonitoring(Configuration("true"));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            var worker = Assert.Single(provider.GetServices<IHostedService>());
            await worker.StartAsync(default);
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(factory.Disposed);
            if (shutdownMode == 1)
            {
                await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(factory.Canceled);
                Assert.True(factory.Disposed);
                Assert.Equal(0, capture.Read()[("sidequest.queue.observation_available", "delivery")]);
            }
            if (shutdownMode == 2)
                provider.Dispose();
            else
                await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(factory.Canceled);
            Assert.True(factory.Disposed);
            Assert.Equal(1, factory.Calls);
            Assert.Empty(capture.Read());
        }
        finally
        {
            await provider.DisposeAsync();
        }
    }

    private static IConfiguration Configuration(string? enabled = null, string? interval = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:Monitoring:Enabled"] = enabled,
            ["Operations:Monitoring:SampleInterval"] = interval
        }.Where(pair => pair.Value is not null)).Build();

    private sealed class ObservedFactory(bool block) : ISidequestDbContextFactory, IAsyncDisposable
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool Canceled { get; private set; }

        /// <inheritdoc/>
        public async Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            if (block)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Canceled = true;
                    throw;
                }
            }
            throw new InvalidOperationException("secret fake endpoint and connection string");
        }

        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Assert.True(!block || Canceled);
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
