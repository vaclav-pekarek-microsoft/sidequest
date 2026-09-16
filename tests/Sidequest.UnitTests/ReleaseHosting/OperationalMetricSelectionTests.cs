using System.Diagnostics.Metrics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Sidequest.Web.Hosting;

namespace Sidequest.UnitTests.ReleaseHosting;

/// <summary>Exercises the production metric source and dimension selection with the real SDK and an entirely in-memory exporter.</summary>
[Collection("Release operational metrics")]
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
        operational.CreateCounter<long>("sidequest.unapproved.private").Add(23,
            new KeyValuePair<string, object?>("user", "private-sentinel"));

        Assert.True(reader.Collect(5_000));
        var reading = Assert.Single(exporter.Readings);
        Assert.Equal("sidequest.queue.pending", reading.Name);
        Assert.Equal(7L, reading.Value);
        var tag = Assert.Single(reading.Tags);
        Assert.Equal("queue", tag.Key);
        Assert.Equal("scheduled", tag.Value);
    }

    /// <summary>Operational counters keep only approved dimensions while request paths, identities and arbitrary tags are dropped.</summary>
    [Fact]
    public void Selection_ExportsOperationAndHttpCountersWithoutPrivateDimensions()
    {
        using var exporter = new CapturingExporter();
        using var reader = new BaseExportingMetricReader(exporter);
        using var provider = AzureHostingRegistration.AddOperationalMetricSources(Sdk.CreateMeterProviderBuilder())
            .AddReader(reader).Build();
        using var operational = new Meter("Sidequest.Operations", "hosting-selection-test");
        operational.CreateCounter<long>("sidequest.operation.completed").Add(3,
            new KeyValuePair<string, object?>("operation", "email_submission"),
            new KeyValuePair<string, object?>("outcome", "uncertain_failure"),
            new KeyValuePair<string, object?>("recipient", "private-sentinel"));
        operational.CreateCounter<long>("sidequest.http.completed").Add(5,
            new KeyValuePair<string, object?>("outcome", "forbidden"),
            new KeyValuePair<string, object?>("path", "/private/resource"),
            new KeyValuePair<string, object?>("user", "private-sentinel"));
        operational.CreateHistogram<double>("sidequest.operation.duration").Record(.25,
            new KeyValuePair<string, object?>("operation", "image_sanitization"),
            new KeyValuePair<string, object?>("outcome", "rejected"),
            new KeyValuePair<string, object?>("url", "/private/resource"));
        Assert.True(reader.Collect(5_000));
        Assert.Equal(3, exporter.Readings.Count);
        var operation = Assert.Single(exporter.Readings, reading => reading.Name == "sidequest.operation.completed");
        Assert.Equal(3, operation.Value);
        Assert.Equal(2, operation.Tags.Count);
        Assert.Equal("email_submission", operation.Tags["operation"]);
        Assert.Equal("uncertain_failure", operation.Tags["outcome"]);
        var http = Assert.Single(exporter.Readings, reading => reading.Name == "sidequest.http.completed");
        Assert.Equal(5, http.Value);
        Assert.Equal("outcome", Assert.Single(http.Tags).Key);
        Assert.Equal("forbidden", http.Tags["outcome"]);
        var duration = Assert.Single(exporter.Readings, reading => reading.Name == "sidequest.operation.duration");
        Assert.Equal(.25, duration.Value);
        Assert.Equal(2, duration.Tags.Count);
        Assert.Equal("image_sanitization", duration.Tags["operation"]);
        Assert.Equal("rejected", duration.Tags["outcome"]);
    }

    private sealed class CapturingExporter : BaseExporter<Metric>
    {
        internal List<(string Name, double Value, Dictionary<string, object?> Tags)> Readings { get; } = [];

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
                    Readings.Add((metric.Name, metric.MetricType switch
                    {
                        MetricType.LongGauge => point.GetGaugeLastValueLong(),
                        MetricType.Histogram => point.GetHistogramSum(),
                        _ => point.GetSumLong()
                    }, tags));
                }
            }
            return ExportResult.Success;
        }
    }
}
