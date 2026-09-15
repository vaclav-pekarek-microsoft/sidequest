using System.Diagnostics.Metrics;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Application.Operations;
using Sidequest.Domain.Model;
using Sidequest.Infrastructure.Operations;
using Sidequest.IntegrationTests.FoundationPersistence;
using Sidequest.Web.Operations;

namespace Sidequest.IntegrationTests.ReleaseOperations;

/// <summary>Validates SQL aggregate semantics, eligibility boundaries, read-only execution and failure honesty in isolated catalogs.</summary>
public sealed class SqlQueueSamplerTests
{
    /// <summary>Empty migrated queues produce three actual zeros, absent oldest ages, and no tracked entities.</summary>
    /// <returns>A task completing after empty aggregates and context disposal are verified.</returns>
    [Fact]
    public async Task EmptyQueuesAreObservedZeros()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        var observations = await new SqlQueueSampler(new SqlOperationalQueueReader(scenario, scenario.Clock)).SampleAsync();
        Assert.Equal(Enum.GetValues<OperationalQueue>(), observations.Select(x => x.Queue));
        Assert.All(observations, row =>
        {
            Assert.Equal(0, row.Pending);
            Assert.Equal(0, row.Due);
            Assert.Equal(0, row.DeadLetter);
            Assert.Null(row.OldestDueAge);
        });
        Assert.Equal(0, scenario.TrackedEntities);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Assert.Single(scenario.Contexts).Users.AnyAsync());
    }

    /// <summary>Pending, retry, exhausted, active/expired/null-lease and terminal states obey queue claim semantics at adjacent SQL timestamp boundaries.</summary>
    /// <returns>A task completing after all three queues and oldest eligibility-age transitions are asserted.</returns>
    [Fact]
    public async Task QueueStatesAndEligibilityBoundaries()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        var now = scenario.Clock.Now;
        var cases = new[]
        {
            (WorkStatus.Pending, now.AddSeconds(-7), (DateTimeOffset?)null, 0),
            (WorkStatus.Pending, now, (DateTimeOffset?)null, 7),
            (WorkStatus.Pending, now.AddTicks(1), (DateTimeOffset?)null, 1),
            (WorkStatus.Pending, now.AddYears(-1), (DateTimeOffset?)null, 8),
            (WorkStatus.Pending, now.AddYears(-1), (DateTimeOffset?)null, 9),
            (WorkStatus.Processing, now.AddYears(-2), (DateTimeOffset?)now.AddSeconds(-3), 7),
            (WorkStatus.Processing, now.AddYears(-2), (DateTimeOffset?)now, 0),
            (WorkStatus.Processing, now.AddYears(-2), (DateTimeOffset?)now.AddTicks(1), 0),
            (WorkStatus.Processing, now.AddYears(-2), (DateTimeOffset?)null, 0),
            (WorkStatus.Processing, now.AddYears(-2), (DateTimeOffset?)now.AddYears(-1), 8),
            (WorkStatus.DeadLetter, now.AddYears(-3), (DateTimeOffset?)null, 8),
            (WorkStatus.Completed, now.AddYears(-3), (DateTimeOffset?)null, 0),
            (WorkStatus.Superseded, now.AddYears(-3), (DateTimeOffset?)null, 0)
        };
        await SeedQueuesAsync(scenario, cases);
        var sampler = new SqlQueueSampler(new SqlOperationalQueueReader(scenario, scenario.Clock));
        AssertQueues(await sampler.SampleAsync(), 5, 4, 1, TimeSpan.FromSeconds(7));
        scenario.Clock.Now += TimeSpan.FromTicks(1);
        AssertQueues(await sampler.SampleAsync(), 5, 6, 1, TimeSpan.FromSeconds(7) + TimeSpan.FromTicks(1));
        scenario.Clock.Now = now.AddSeconds(-7).AddTicks(-1);
        var before = await sampler.SampleAsync();
        Assert.All(before, row => { Assert.Equal(0, row.Due); Assert.Null(row.OldestDueAge); });
        scenario.Clock.Now = now.AddSeconds(-7);
        AssertQueues(await sampler.SampleAsync(), 5, 1, 1, TimeSpan.Zero);
    }

    /// <summary>Different queue states retain their own fixed dimension; processing-only backlog may exceed the Pending count.</summary>
    /// <returns>A task completing after independent exact aggregates identify each physical queue.</returns>
    [Fact]
    public async Task QueueDimensionsPreserveIndependentAggregates()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await SeedQueuesAsync(scenario, [(WorkStatus.Pending, scenario.Clock.Now, null, 0)]);
        await using (var setup = scenario.Database.CreateContext())
        {
            setup.OutboxMessages.Add(new OutboxMessage { DueUtc = scenario.Clock.Now.AddHours(1) });
            (await setup.ScheduledWork.SingleAsync()).Status = WorkStatus.DeadLetter;
            var delivery = await setup.NotificationDeliveries.SingleAsync();
            delivery.Status = WorkStatus.Processing;
            delivery.LeaseUntilUtc = scenario.Clock.Now.AddSeconds(-5);
            await setup.SaveChangesAsync();
        }
        var rows = await new SqlQueueSampler(new SqlOperationalQueueReader(scenario, scenario.Clock)).SampleAsync();
        Assert.Equal(new QueueObservation(OperationalQueue.Outbox, 2, 1, 0, TimeSpan.Zero), rows[0]);
        Assert.Equal(new QueueObservation(OperationalQueue.Scheduled, 0, 0, 1, null), rows[1]);
        Assert.Equal(new QueueObservation(OperationalQueue.Delivery, 0, 1, 0, TimeSpan.FromSeconds(5)), rows[2]);
    }

    /// <summary>A SELECT-only principal can sample nonempty queues, with unchanged rowversions and payloads and no tracked objects or saves.</summary>
    /// <returns>A task completing after SQL authorization and exact before/after persisted state checks.</returns>
    [Fact]
    public async Task SamplingDoesNotMutateOrTrack()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await SeedQueuesAsync(scenario, [(WorkStatus.Pending, scenario.Clock.Now.AddSeconds(-2), null, 1)]);
        await using var read = scenario.Database.CreateContext();
        var beforeOutbox = await read.OutboxMessages.AsNoTracking().SingleAsync();
        var beforeSchedule = await read.ScheduledWork.AsNoTracking().SingleAsync();
        var beforeDelivery = await read.NotificationDeliveries.AsNoTracking().SingleAsync();
        await read.Database.ExecuteSqlRawAsync("CREATE USER [OperationalReadOnly] WITHOUT LOGIN; GRANT SELECT TO [OperationalReadOnly];");
        scenario.ReadOnlyUser = true;

        AssertQueues(await new SqlQueueSampler(new SqlOperationalQueueReader(scenario, scenario.Clock)).SampleAsync(), 1, 1, 0, TimeSpan.FromSeconds(2));

        var afterOutbox = await read.OutboxMessages.AsNoTracking().SingleAsync();
        var afterSchedule = await read.ScheduledWork.AsNoTracking().SingleAsync();
        var afterDelivery = await read.NotificationDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal(beforeOutbox.Version, afterOutbox.Version);
        Assert.Equal(beforeSchedule.Version, afterSchedule.Version);
        Assert.Equal(beforeDelivery.Version, afterDelivery.Version);
        Assert.Equal(beforeOutbox.PayloadJson, afterOutbox.PayloadJson);
        Assert.Equal(beforeSchedule.PayloadJson, afterSchedule.PayloadJson);
        Assert.Equal(beforeDelivery.PayloadJson, afterDelivery.PayloadJson);
        Assert.Equal(0, scenario.TrackedEntities);
        Assert.Equal(0, scenario.Saves);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Assert.Single(scenario.Contexts).Users.AnyAsync());
    }

    /// <summary>A real missing queue table yields no partial sample or healthy zeros; restoration permits a new complete nonzero observation.</summary>
    /// <returns>A task completing after success/failure/recovery gauge transitions backed by actual SQL results.</returns>
    [Fact]
    public async Task FailedSqlSamplingDoesNotProduceSuccessShapedZerosAndRecovers()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        await SeedQueuesAsync(scenario, [(WorkStatus.Pending, scenario.Clock.Now, null, 0)]);
        var values = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == OperationalQueueMetrics.MeterName)
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Assert.Equal("queue", tags[0].Key);
            values.Add(instrument.Name + ":" + tags[0].Value, value);
        });
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            values.Add(instrument.Name + ":" + tags[0].Value, value));
        listener.Start();
        using var logs = new SafeLogCapture();
        var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logs))
            .AddSingleton<TimeProvider>(scenario.Clock)
            .AddScoped<ISidequestDbContextFactory>(_ => new ScenarioFactory(scenario));
        services.AddSidequestOperationalPersistence();
        services.AddSidequestOperationalMonitoring(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Operations:Monitoring:Enabled"] = "true",
            ["Operations:Monitoring:SampleInterval"] = "00:00:05"
        }).Build());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(default);
        var timer = await scenario.Clock.NextTimerAsync();
        listener.RecordObservableInstruments();
        Assert.Equal(1, values["sidequest.queue.pending:scheduled"]);
        await using var setup = scenario.Database.CreateContext();
        await setup.Database.ExecuteSqlRawAsync("EXEC sp_rename 'ScheduledWork', 'TemporarilyUnavailableWork'");
        try
        {
            IOperationalQueueReader reader = new SqlOperationalQueueReader(scenario, scenario.Clock);
            var failure = await Assert.ThrowsAsync<OperationalObservationException>(() => reader.ReadAsync());
            Assert.Equal(OperationalFailureKind.SchemaUnavailable, failure.Kind);
            Assert.Equal("Operational observation is unavailable.", failure.Message);
            Assert.Null(failure.InnerException);
            timer.Fire();
            timer = await scenario.Clock.NextTimerAsync();
            values.Clear();
            listener.RecordObservableInstruments();
            Assert.Equal(6, values.Count);
            Assert.Equal(0, values["sidequest.queue.observation_available:scheduled"]);
            Assert.False(values.ContainsKey("sidequest.queue.pending:scheduled"));
        }
        finally
        {
            await setup.Database.ExecuteSqlRawAsync("EXEC sp_rename 'TemporarilyUnavailableWork', 'ScheduledWork'");
        }
        timer.Fire();
        await scenario.Clock.NextTimerAsync();
        values.Clear();
        listener.RecordObservableInstruments();
        Assert.Equal(1, values["sidequest.queue.observation_available:scheduled"]);
        Assert.Equal(1, values["sidequest.queue.pending:scheduled"]);
        Assert.Equal(1, values["sidequest.queue.due:scheduled"]);
        var failureLog = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("Operational SQL observation unavailable: schema-unavailable.", failureLog.Message);
        Assert.All(logs.Entries, entry => Assert.Null(entry.Exception));
        await worker.StopAsync(default);
        Assert.Equal(0, scenario.TrackedEntities);
        Assert.Equal(0, scenario.Saves);
    }

    /// <summary>Sampling rejects application ambient transactions before connecting and propagates pre-cancellation without creating another context.</summary>
    /// <returns>A task completing after transaction rejection and exact cancellation-token assertions.</returns>
    [Fact]
    public async Task SamplingRejectsAmbientTransactionAndPropagatesCancellation()
    {
        await using var scenario = await OperationalSqlScenario.CreateAsync();
        var sampler = new SqlQueueSampler(new SqlOperationalQueueReader(scenario, scenario.Clock));
        using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var unavailable = await Assert.ThrowsAsync<OperationalObservationException>(() => sampler.SampleAsync());
            Assert.Equal(OperationalFailureKind.Configuration, unavailable.Kind);
            Assert.Null(unavailable.InnerException);
        }
        Assert.Single(scenario.Contexts);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sampler.SampleAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Single(scenario.Contexts);
        AssertQueues(await sampler.SampleAsync(), 0, 0, 0, null);
    }

    private static void AssertQueues(IReadOnlyList<QueueObservation> observations, long pending, long due, long dead, TimeSpan? age)
    {
        Assert.Equal(Enum.GetValues<OperationalQueue>(), observations.Select(x => x.Queue));
        Assert.All(observations, row =>
        {
            Assert.Equal(pending, row.Pending);
            Assert.Equal(due, row.Due);
            Assert.Equal(dead, row.DeadLetter);
            Assert.Equal(age, row.OldestDueAge);
        });
    }

    private static async Task SeedQueuesAsync(OperationalSqlScenario scenario,
        (WorkStatus Status, DateTimeOffset Due, DateTimeOffset? LeaseUntil, int Attempts)[] states)
    {
        await using var setup = scenario.Database.CreateContext();
        var user = FoundationSeed.NewUser();
        setup.Users.Add(user);
        await setup.SaveChangesAsync();
        var notification = new Notification { UserId = user.Id, SourceChangeId = Guid.NewGuid(), CreatedUtc = scenario.Clock.Now };
        setup.Notifications.Add(notification);
        await setup.SaveChangesAsync();
        foreach (var (status, due, lease, attempts) in states)
        {
            const string payload = "{\"private-roster\":\"never-materialize\",\"businessDeadlineUtc\":\"2000-01-01T00:00:00Z\"}";
            setup.OutboxMessages.Add(new OutboxMessage
            {
                Status = status, DueUtc = due, LeaseUntilUtc = lease, Attempts = attempts,
                PayloadJson = payload, OccurredUtc = scenario.Clock.Now.AddYears(-5), Type = "test.v1"
            });
            setup.ScheduledWork.Add(new ScheduledWork
            {
                Status = status, DueUtc = due, LeaseUntilUtc = lease, Attempts = attempts,
                DeduplicationKey = Guid.NewGuid().ToString(), PayloadJson = payload, Type = "test.v1"
            });
            setup.NotificationDeliveries.Add(new NotificationDelivery
            {
                NotificationId = notification.Id, UserId = user.Id,
                Status = status, DueUtc = due, LeaseUntilUtc = lease, Attempts = attempts,
                DeduplicationKey = Guid.NewGuid().ToString(), PayloadJson = payload
            });
        }
        await setup.SaveChangesAsync();
    }

    private sealed class ScenarioFactory(OperationalSqlScenario scenario) : ISidequestDbContextFactory
    {
        /// <inheritdoc/>
        public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default) => scenario.CreateAsync(cancellationToken);
    }

    private sealed class SafeLogCapture : ILoggerProvider
    {
        internal List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        /// <inheritdoc/>
        public void Dispose() { }

        private sealed class Sink(SafeLogCapture owner) : ILogger
        {
            /// <inheritdoc/>
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            /// <inheritdoc/>
            public bool IsEnabled(LogLevel logLevel) => true;
            /// <inheritdoc/>
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
