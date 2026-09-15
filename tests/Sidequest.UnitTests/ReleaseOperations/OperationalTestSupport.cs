using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sidequest.Application.Abstractions;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.ReleaseOperations;

internal sealed class OperationalTestClock : TimeProvider
{
    private readonly Channel<ManualTimer> timers = Channel.CreateUnbounded<ManualTimer>();
    private long ticks;
    internal DateTimeOffset UtcOrigin { get; set; } = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    /// <inheritdoc/>
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    /// <inheritdoc/>
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => UtcOrigin + TimeSpan.FromTicks(GetTimestamp());
    internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    internal async Task<ManualTimer> NextTimerAsync() =>
        await timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime);
        Assert.True(timers.Writer.TryWrite(timer));
        return timer;
    }

    internal sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        private int disposed;
        internal TimeSpan Due { get; } = due;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
        internal void Fire()
        {
            if (!IsDisposed)
                callback(state);
        }
        /// <inheritdoc/>
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
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

internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly Dictionary<(string Instrument, string Queue), double> values = [];

    internal MetricCapture()
    {
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == OperationalQueueMetrics.MeterName)
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        listener.Start();
    }

    internal Dictionary<(string Instrument, string Queue), double> Read()
    {
        values.Clear();
        listener.RecordObservableInstruments();
        return new(values);
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Assert.Equal(1, tags.Length);
        Assert.Equal("queue", tags[0].Key);
        var queue = Assert.IsType<string>(tags[0].Value);
        Assert.Contains(queue, new[] { "outbox", "scheduled", "delivery" });
        Assert.True(values.TryAdd((instrument.Name, queue), value), "Duplicate queue measurement in one collection.");
    }

    /// <inheritdoc/>
    public void Dispose() => listener.Dispose();
}

internal sealed class OperationalLogs : ILoggerProvider
{
    internal ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
    internal TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Sink(this);
    /// <inheritdoc/>
    public void Dispose() { }

    private sealed class Sink(OperationalLogs owner) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            owner.Entries.Enqueue((logLevel, formatter(state, exception), exception));
            owner.Logged.TrySetResult();
        }
    }
}

internal sealed class FailingContextFactory(Exception failure) : ISidequestDbContextFactory
{
    internal int Calls { get; private set; }
    /// <inheritdoc/>
    public Task<ISidequestDbContext> CreateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromException<ISidequestDbContext>(failure);
    }
}
