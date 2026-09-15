using System.Diagnostics.Metrics;
using Sidequest.Application.Operations;

namespace Sidequest.Web.Operations;

/// <summary>Publishes cached aggregate gauges only; collecting metrics never queries SQL or exposes identifiers.</summary>
/// <remarks>
/// Thread-safe publication copies input. Backlog gauges are absent before success, after failure and when stale;
/// actual zero counts appear only for a fresh successful sample. Observation age remains visible after failure.
/// Dispose after stopping the hosted sampler; disposal is idempotent and removes the owned Meter.
/// </remarks>
public sealed class OperationalQueueMetrics : IDisposable
{
    private readonly object sync = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan staleAfter;
    private readonly Meter meter;
    private QueueObservation[]? latest;
    private long? observedAt;
    private bool available;
    private bool disposed;

    /// <summary>The meter name exporters must explicitly subscribe to; instruments are versioned with this code.</summary>
    public const string MeterName = "Sidequest.Operations";

    /// <summary>Creates the owned meter and fixed queue gauges; no database or exporter is activated.</summary>
    /// <param name="clock">Monotonic elapsed time provider used for staleness and sample age.</param>
    /// <param name="options">Validated sampling policy defining twice-interval staleness.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public OperationalQueueMetrics(TimeProvider clock, OperationalMonitoringOptions options)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        this.clock = clock;
        staleAfter = options.StaleAfter;
        meter = new Meter(MeterName, "1.0.0");
        meter.CreateObservableGauge("sidequest.queue.pending", () => Counts(x => x.Pending), "{work}",
            "Rows in Pending state in a fresh successful observation.");
        meter.CreateObservableGauge("sidequest.queue.due", () => Counts(x => x.Due), "{work}",
            "Eligible pending or expired-lease rows with attempts remaining.");
        meter.CreateObservableGauge("sidequest.queue.dead_letter", () => Counts(x => x.DeadLetter), "{work}",
            "Rows already persisted in DeadLetter state.");
        meter.CreateObservableGauge("sidequest.queue.oldest_due_age", OldestDueAge, "s",
            "Age at sampling of oldest retry/lease eligibility; absent when no work is due.");
        meter.CreateObservableGauge("sidequest.queue.observation_available", Availability, "1",
            "1 only after a successful sample which is not stale; otherwise 0.");
        meter.CreateObservableGauge("sidequest.queue.observation_stale", Staleness, "1",
            "1 before any successful sample or when its age exceeds twice the interval; otherwise 0.");
        meter.CreateObservableGauge("sidequest.queue.observation_age", ObservationAge, "s",
            "Elapsed age of last success, including during failure; absent before first success.");
    }

    /// <summary>Atomically replaces all three queue observations and marks sampling successful.</summary>
    /// <param name="observations">Exactly one observation of each fixed queue; copied before publication.</param>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentException">The collection is incomplete, duplicated or contains null.</exception>
    /// <exception cref="ObjectDisposedException">The meter has been disposed.</exception>
    public void RecordSuccess(IReadOnlyList<QueueObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var copy = observations.ToArray();
        if (copy.Length != 3 || copy.Any(x => x is null) || copy.Select(x => x.Queue).Distinct().Count() != 3)
            throw new ArgumentException("A sample must contain each of the three operational queues exactly once.", nameof(observations));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            latest = copy;
            observedAt = clock.GetTimestamp();
            available = true;
        }
    }

    /// <summary>Marks sampling unavailable without publishing zeros or resetting the age of the last successful observation.</summary>
    /// <exception cref="ObjectDisposedException">The meter has been disposed.</exception>
    public void RecordFailure()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            available = false;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            latest = null;
        }
        // Meter callbacks use sync; do not hold it while unregistering listeners.
        meter.Dispose();
    }

    private Measurement<long>[] Counts(Func<QueueObservation, long> value)
    {
        lock (sync)
            return IsAvailable() ? latest!.Select(x => new Measurement<long>(value(x), Tag(x.Queue))).ToArray() : [];
    }

    private Measurement<double>[] OldestDueAge()
    {
        lock (sync)
            return IsAvailable() ? latest!.Where(x => x.OldestDueAge.HasValue)
                .Select(x => new Measurement<double>(x.OldestDueAge!.Value.TotalSeconds, Tag(x.Queue))).ToArray() : [];
    }

    private Measurement<int>[] Availability()
    {
        lock (sync)
            return disposed ? [] : ForAll(IsAvailable() ? 1 : 0);
    }

    private Measurement<int>[] Staleness()
    {
        lock (sync)
            return disposed ? [] : ForAll(IsStale() ? 1 : 0);
    }

    private Measurement<double>[] ObservationAge()
    {
        lock (sync)
            return disposed || observedAt is null ? [] : Enum.GetValues<OperationalQueue>()
                .Select(x => new Measurement<double>(Age().TotalSeconds, Tag(x))).ToArray();
    }

    private bool IsAvailable() => !disposed && available && !IsStale();
    private bool IsStale() => observedAt is null || Age() > staleAfter;
    private TimeSpan Age() => clock.GetElapsedTime(observedAt!.Value, clock.GetTimestamp());

    private static Measurement<int>[] ForAll(int value) => Enum.GetValues<OperationalQueue>()
        .Select(x => new Measurement<int>(value, Tag(x))).ToArray();

    private static KeyValuePair<string, object?> Tag(OperationalQueue queue) => new("queue", queue switch
    {
        OperationalQueue.Outbox => "outbox",
        OperationalQueue.Scheduled => "scheduled",
        OperationalQueue.Delivery => "delivery",
        _ => throw new ArgumentOutOfRangeException(nameof(queue))
    });
}
