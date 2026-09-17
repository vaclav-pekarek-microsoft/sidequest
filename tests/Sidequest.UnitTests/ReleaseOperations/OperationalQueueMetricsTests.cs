using Sidequest.Application.Operations;
using Sidequest.Web.Operations;

namespace Sidequest.UnitTests.ReleaseOperations;

/// <summary>Verifies truthful aggregate instruments, elapsed-time staleness and resource disposal without SQL or real delays.</summary>
[Collection("Release operational metrics")]
public sealed class OperationalQueueMetricsTests
{
    /// <summary>Distinguishes never-observed and failed samples from actual empty queues and recovers without retaining old counts.</summary>
    [Fact]
    public void MissingFailedAndRecoveredObservations()
    {
        using var capture = new MetricCapture();
        var clock = new OperationalTestClock();
        using var metrics = new OperationalQueueMetrics(clock, new());
        AssertUnavailable(capture.Read(), stale: 1, age: null);
        metrics.RecordFailure();
        AssertUnavailable(capture.Read(), stale: 1, age: null);

        metrics.RecordSuccess(Sample(4));
        clock.Advance(TimeSpan.FromSeconds(5));
        metrics.RecordFailure();
        AssertUnavailable(capture.Read(), stale: 0, age: 5);
        clock.Advance(TimeSpan.FromSeconds(56));
        AssertUnavailable(capture.Read(), stale: 1, age: 61);

        metrics.RecordSuccess(Sample(0));
        var recovered = capture.Read();
        foreach (var queue in Queues)
        {
            Assert.Equal(1, recovered[("sidequest.queue.observation_available", queue)]);
            Assert.Equal(0, recovered[("sidequest.queue.observation_stale", queue)]);
            Assert.Equal(0, recovered[("sidequest.queue.observation_age", queue)]);
            Assert.Equal(0, recovered[("sidequest.queue.pending", queue)]);
            Assert.Equal(0, recovered[("sidequest.queue.due", queue)]);
            Assert.Equal(0, recovered[("sidequest.queue.dead_letter", queue)]);
            Assert.False(recovered.ContainsKey(("sidequest.queue.oldest_due_age", queue)));
        }
        Assert.Equal(18, recovered.Count);
    }

    /// <summary>Backlog remains usable at the exact threshold and disappears one monotonic tick later, regardless of UTC clock adjustments.</summary>
    [Fact]
    public void StalenessBoundarySuppressesBacklog()
    {
        using var capture = new MetricCapture();
        var clock = new OperationalTestClock();
        using var metrics = new OperationalQueueMetrics(clock, new(sampleInterval: TimeSpan.FromSeconds(5)));
        metrics.RecordSuccess(Sample(2));
        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.Equal(1, capture.Read()[("sidequest.queue.observation_available", "outbox")]);
        clock.Advance(TimeSpan.FromTicks(1));
        clock.UtcOrigin -= TimeSpan.FromDays(365);
        var boundary = capture.Read();
        Assert.Equal(2, boundary[("sidequest.queue.pending", "outbox")]);
        Assert.Equal(10, boundary[("sidequest.queue.observation_age", "outbox")]);
        Assert.Equal(7, boundary[("sidequest.queue.oldest_due_age", "outbox")]);
        clock.Advance(TimeSpan.FromTicks(1));
        AssertUnavailable(capture.Read(), stale: 1, age: 10.0000001);
    }

    /// <summary>Snapshots copy caller collections, preserve each queue independently and replace rather than accumulate repeated counts.</summary>
    [Fact]
    public void RepeatedObservationsReplaceCountsAndCopyInput()
    {
        using var capture = new MetricCapture();
        using var metrics = new OperationalQueueMetrics(new OperationalTestClock(), new());
        var input = new[]
        {
            new QueueObservation(OperationalQueue.Outbox, 11, 3, 4, TimeSpan.FromSeconds(7)),
            new QueueObservation(OperationalQueue.Scheduled, 12, 5, 6, TimeSpan.Zero),
            new QueueObservation(OperationalQueue.Delivery, 13, 0, 8, null)
        };
        metrics.RecordSuccess(input);
        input[0] = new(OperationalQueue.Outbox, 999, 0, 0, null);
        var first = capture.Read();
        Assert.Equal(11, first[("sidequest.queue.pending", "outbox")]);
        Assert.Equal(3, first[("sidequest.queue.due", "outbox")]);
        Assert.Equal(4, first[("sidequest.queue.dead_letter", "outbox")]);
        Assert.Equal(12, first[("sidequest.queue.pending", "scheduled")]);
        Assert.Equal(5, first[("sidequest.queue.due", "scheduled")]);
        Assert.Equal(6, first[("sidequest.queue.dead_letter", "scheduled")]);
        Assert.Equal(0, first[("sidequest.queue.oldest_due_age", "scheduled")]);
        Assert.Equal(13, first[("sidequest.queue.pending", "delivery")]);
        Assert.Equal(8, first[("sidequest.queue.dead_letter", "delivery")]);
        Assert.False(first.ContainsKey(("sidequest.queue.oldest_due_age", "delivery")));
        metrics.RecordSuccess(Sample(1));
        metrics.RecordSuccess(Sample(1));
        foreach (var queue in Queues)
            Assert.Equal(1, capture.Read()[("sidequest.queue.pending", queue)]);
    }

    /// <summary>Meter disposal unregisters all gauges and rejects late publication; repeated disposal is harmless.</summary>
    [Fact]
    public void DisposedMeterStopsPublishing()
    {
        using var capture = new MetricCapture();
        var metrics = new OperationalQueueMetrics(new OperationalTestClock(), new());
        metrics.RecordSuccess(Sample(1));
        Assert.Equal(21, capture.Read().Count);
        metrics.Dispose();
        metrics.Dispose();
        Assert.Empty(capture.Read());
        Assert.Throws<ObjectDisposedException>(() => metrics.RecordFailure());
        Assert.Throws<ObjectDisposedException>(() => metrics.RecordSuccess(Sample(2)));
    }

    /// <summary>Partial, duplicated and null samples cannot overwrite the last complete successful observation.</summary>
    [Fact]
    public void InvalidSamplesDoNotReplaceValidObservation()
    {
        using var capture = new MetricCapture();
        using var metrics = new OperationalQueueMetrics(new OperationalTestClock(), new());
        metrics.RecordSuccess(Sample(3));
        Assert.Throws<ArgumentNullException>(() => metrics.RecordSuccess(null!));
        Assert.Throws<ArgumentException>(() => metrics.RecordSuccess([]));
        Assert.Throws<ArgumentException>(() => metrics.RecordSuccess([Sample(1)[0]]));
        Assert.Throws<ArgumentException>(() => metrics.RecordSuccess([Sample(1)[0], Sample(1)[0], Sample(1)[2]]));
        Assert.Throws<ArgumentException>(() => metrics.RecordSuccess([Sample(1)[0], null!, Sample(1)[2]]));
        Assert.Equal(3, capture.Read()[("sidequest.queue.pending", "outbox")]);
        Assert.Throws<ArgumentNullException>(() => new OperationalQueueMetrics(null!, new()));
        Assert.Throws<ArgumentNullException>(() => new OperationalQueueMetrics(TimeProvider.System, null!));
    }

    /// <summary>Rejects undefined queue dimensions, impossible negative aggregates and inconsistent eligibility-age presence.</summary>
    [Fact]
    public void QueueObservationRejectsInvalidPartitions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueObservation((OperationalQueue)3, 0, 0, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueObservation(OperationalQueue.Outbox, -1, 0, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueObservation(OperationalQueue.Outbox, 0, -1, 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueObservation(OperationalQueue.Outbox, 0, 0, -1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueueObservation(OperationalQueue.Outbox, 0, 1, 0, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentException>(() => new QueueObservation(OperationalQueue.Outbox, 0, 0, 0, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new QueueObservation(OperationalQueue.Outbox, 0, 1, 0, null));
        var expiredLeaseOnly = new QueueObservation(OperationalQueue.Delivery, 0, 1, 0, TimeSpan.Zero);
        Assert.Equal(0, expiredLeaseOnly.Pending);
        Assert.Equal(1, expiredLeaseOnly.Due);
        Assert.Equal(TimeSpan.Zero, expiredLeaseOnly.OldestDueAge);
    }

    private static readonly string[] Queues = ["outbox", "scheduled", "delivery"];
    private static QueueObservation[] Sample(long count) => Enum.GetValues<OperationalQueue>()
        .Select(queue => new QueueObservation(queue, count, count, count, count == 0 ? null : TimeSpan.FromSeconds(7))).ToArray();

    private static void AssertUnavailable(Dictionary<(string Instrument, string Queue), double> values, int stale, double? age)
    {
        Assert.Equal(age is null ? 6 : 9, values.Count);
        foreach (var queue in Queues)
        {
            Assert.Equal(0, values[("sidequest.queue.observation_available", queue)]);
            Assert.Equal(stale, values[("sidequest.queue.observation_stale", queue)]);
            if (age is not null)
                Assert.Equal(age.Value, values[("sidequest.queue.observation_age", queue)], 7);
        }
    }
}
