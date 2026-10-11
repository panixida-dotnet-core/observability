using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class BackgroundTelemetryTests
{
    [Theory(DisplayName = "Quartz duration histograms resolve subsecond jobs without changing other meters")]
    [InlineData("Quartz", "quartz.job.execution.duration", "s", true)]
    [InlineData("Quartz", "quartz.trigger.acquisition.duration", "s", true)]
    [InlineData("Quartz", "quartz.jobstore.operation.duration", "s", true)]
    [InlineData("Quartz", "quartz.jobstore.lock.wait.duration", "s", true)]
    [InlineData("Quartz", "quartz.cluster.checkin.duration", "s", true)]
    [InlineData("Quartz", "quartz.job.execution.duration", "ms", false)]
    [InlineData("Wolverine:Orders", "quartz.job.execution.duration", "s", false)]
    public void QuartzDurationBucketsShouldUseSeconds(string meterName, string metricName, string unit, bool targeted)
    {
        var buckets = new List<(double Bound, long Count)>();
        using var exporter = new RecordingExporter<Metric>(metric =>
        {
            if (metric.Name != metricName || metric.MeterName != meterName)
            {
                return;
            }

            metric.Unit.Should().Be(unit);
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                point.GetHistogramCount().Should().Be(2);
                point.GetHistogramSum().Should().BeApproximately(0.15, 0.000001);
                foreach (var bucket in point.GetHistogramBuckets())
                {
                    buckets.Add((bucket.ExplicitBound, bucket.BucketCount));
                }
            }
        });
        using var reader = new BaseExportingMetricReader(exporter);
        var builder = CreateBuilder();
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(reader));
        using var app = builder.Build();
        _ = app.Services.GetRequiredService<MeterProvider>();
        using var meter = new Meter(meterName);
        var histogram = meter.CreateHistogram<double>(metricName, unit);

        histogram.Record(0.0005);
        histogram.Record(0.1495);
        reader.Collect().Should().BeTrue();

        buckets.Should().NotBeEmpty();
        if (targeted)
        {
            buckets.Should().Contain((0.001, 1L)).And.Contain((0.25, 1L));
            buckets.Should().Contain(item => item.Bound == 3600);
        }
        else
        {
            buckets.Should().NotContain(item => item.Bound == 0.001);
        }
    }

    [Theory(DisplayName = "AddObservability collects Wolverine and Quartz meters")]
    [InlineData("Wolverine:Orders", "wolverine-messages-succeeded")]
    [InlineData("Wolverine:Notifications", "wolverine-messages-succeeded")]
    [InlineData("Quartz", "quartz.trigger.misfire")]
    public void AddObservability_ShouldCollectBackgroundMetrics(string meterName, string metricName)
    {
        var measurements = new ConcurrentBag<(string Meter, long Value)>();
        using var exporter = new RecordingExporter<Metric>(metric =>
        {
            if (metric.Name != metricName)
            {
                return;
            }

            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                measurements.Add((metric.MeterName, point.GetSumLong()));
            }
        });
        using var reader = new BaseExportingMetricReader(exporter);
        var builder = CreateBuilder();
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(reader));

        using var app = builder.Build();
        _ = app.Services.GetRequiredService<MeterProvider>();
        using var meter = new Meter(meterName);
        using var unrelatedMeter = new Meter("Unrelated.Library");

        meter.CreateCounter<long>(metricName).Add(3);
        unrelatedMeter.CreateCounter<long>(metricName).Add(7);
        reader.Collect().Should().BeTrue();

        measurements.Should().ContainSingle().Which.Should().Be((meterName, 3L));
    }

    [Theory(DisplayName = "AddObservability collects Wolverine and Quartz spans with their parent context")]
    [InlineData("Wolverine", "message processing", ActivityKind.Consumer)]
    [InlineData("Quartz", "Quartz.Job.Execute", ActivityKind.Internal)]
    public void AddObservability_ShouldCollectBackgroundTraces(string sourceName, string operationName, ActivityKind kind)
    {
        var activities = new ConcurrentBag<Activity>();
        using var exporter = new RecordingExporter<Activity>(activities.Add);
        using var processor = new SimpleActivityExportProcessor(exporter);
        var builder = CreateBuilder();
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(processor));

        using var app = builder.Build();
        _ = app.Services.GetRequiredService<TracerProvider>();
        using var source = new ActivitySource(sourceName);
        var parent = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded);

        using (var activity = source.StartActivity(operationName, kind, parent))
        {
            activity.Should().NotBeNull();
        }

        var exportedActivity = activities.Should().ContainSingle().Subject;
        exportedActivity.Source.Name.Should().Be(sourceName);
        exportedActivity.OperationName.Should().Be(operationName);
        exportedActivity.Kind.Should().Be(kind);
        exportedActivity.TraceId.Should().Be(parent.TraceId);
        exportedActivity.ParentSpanId.Should().Be(parent.SpanId);
    }

    [Fact(DisplayName = "AddObservability already collects Wolverine logs and respects logging filters")]
    public void AddObservability_ShouldCollectWolverineLogs()
    {
        var records = new ConcurrentBag<(string? Category, LogLevel Level, string? Message)>();
        using var exporter = new RecordingExporter<LogRecord>(record =>
            records.Add((record.CategoryName, record.LogLevel, record.FormattedMessage)));
        using var processor = new SimpleLogRecordExportProcessor(exporter);
        var builder = CreateBuilder();
        builder.Logging.AddFilter("Wolverine", LogLevel.Warning);
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithLogging(logging => logging.AddProcessor(processor));

        using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Wolverine.Runtime");

        logger.LogInformation("Message processed");
        logger.LogWarning("Message moved to dead letter queue");

        records.Should().ContainSingle().Which.Should().Be(
            ("Wolverine.Runtime", LogLevel.Warning, "Message moved to dead letter queue"));
    }

    private static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(BackgroundTelemetryTests).Assembly.FullName,
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
            ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1"
        });
        return builder;
    }

    private sealed class RecordingExporter<T>(Action<T> record) : BaseExporter<T>
        where T : class
    {
        public override ExportResult Export(in Batch<T> batch)
        {
            foreach (var item in batch)
            {
                record(item);
            }

            return ExportResult.Success;
        }
    }
}
