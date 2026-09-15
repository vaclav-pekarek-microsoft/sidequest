using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Sidequest.Web.Hosting;

namespace Sidequest.UnitTests.ReleaseHosting;

/// <summary>Exercises the production metric source and dimension selection with the real SDK and an entirely in-memory exporter.</summary>
public sealed class OperationalMetricSelectionTests
{
    /// <summary>Queue values and their fixed dimension survive export while arbitrary private tags and unrelated HTTP instrumentation do not.</summary>
    [Fact]
    public void Selection_ExportsQueueValueWithoutPrivateTagsOrOtherMeters()
    {
        using var exporter = new CapturingExporter();
        using var reader = new BaseExportingMetricReader(exporter);
        using var provider = AzureHostingRegistration.AddOperationalMetricSources(Sdk.CreateMeterProviderBuilder())
            .AddReader(reader).Build();
        using var operational = new Meter("Sidequest.Operations", "hosting-selection-test");
        using var unrelated = new Meter("Microsoft.AspNetCore.Hosting", "hosting-selection-test");
        operational.CreateObservableGauge("sidequest.queue.pending", () => new Measurement<long>(7,
            new KeyValuePair<string, object?>("queue", "scheduled"),
            new KeyValuePair<string, object?>("user", "private-sentinel")));
        unrelated.CreateCounter<long>("synthetic.hosting.request").Add(19);

        Assert.True(reader.Collect(5_000));
        var reading = Assert.Single(exporter.Readings);
        Assert.Equal("sidequest.queue.pending", reading.Name);
        Assert.Equal(7L, reading.Value);
        var tag = Assert.Single(reading.Tags);
        Assert.Equal("queue", tag.Key);
        Assert.Equal("scheduled", tag.Value);
    }

    private sealed class CapturingExporter : BaseExporter<Metric>
    {
        internal List<(string Name, long Value, Dictionary<string, object?> Tags)> Readings { get; } = [];

        /// <inheritdoc />
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MeterVersion != "hosting-selection-test")
                    continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, object?>();
                    foreach (var tag in point.Tags)
                        tags.Add(tag.Key, tag.Value);
                    Readings.Add((metric.Name, metric.MetricType == MetricType.LongGauge
                        ? point.GetGaugeLastValueLong() : point.GetSumLong(), tags));
                }
            }
            return ExportResult.Success;
        }
    }
}
