using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Sidequest.Application.Abstractions;
using Sidequest.Infrastructure.Persistence;
using Sidequest.IntegrationTests.FoundationPersistence;

namespace Sidequest.IntegrationTests.ReleaseOperations;

internal sealed class OperationalSqlScenario : ISidequestDbContextFactory, IAsyncDisposable
{
    internal SqlTestDatabase Database { get; } = new();
    internal ObservationClock Clock { get; } = new();
    internal List<SidequestDbContext> Contexts { get; } = [];
    internal int TrackedEntities { get; private set; }
    internal int Saves { get; private set; }
    internal bool ReadOnlyUser { get; set; }

    internal static async Task<OperationalSqlScenario> CreateAsync()
    {
        var scenario = new OperationalSqlScenario();
        await scenario.Database.InitializeAsync();
        return scenario;
    }

    /// <inheritdoc/>
    public async Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = Database.CreateContext();
        Contexts.Add(context);
        context.ChangeTracker.Tracked += (_, _) => TrackedEntities++;
        context.SavingChanges += (_, _) => Saves++;
        try
        {
            if (ReadOnlyUser)
            {
                await context.Database.OpenConnectionAsync(cancellationToken);
                await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = 'OperationalReadOnly'", cancellationToken);
            }
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(Database.DisposeAsync());

    internal sealed class ObservationClock : TimeProvider
    {
        private readonly Channel<ManualTimer> timers = Channel.CreateUnbounded<ManualTimer>();
        internal DateTimeOffset Now { get; set; } = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        /// <inheritdoc/>
        public override DateTimeOffset GetUtcNow() => Now;

        internal async Task<ManualTimer> NextTimerAsync() =>
            await timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        /// <inheritdoc/>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            Assert.True(timers.Writer.TryWrite(timer));
            return timer;
        }
    }

    internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private int disposed;
        internal void Fire()
        {
            if (Volatile.Read(ref disposed) == 0)
                callback(state);
        }
        /// <inheritdoc/>
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref disposed) == 0;
        /// <inheritdoc/>
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        /// <inheritdoc/>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
