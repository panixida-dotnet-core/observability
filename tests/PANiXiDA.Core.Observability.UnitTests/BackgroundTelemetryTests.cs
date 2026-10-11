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
        reader.Collect().ShouldBeTrue();

        measurements.ShouldHaveSingleItem().ShouldBe((meterName, 3L));
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
            activity.ShouldNotBeNull();
        }

        var exportedActivity = activities.ShouldHaveSingleItem();
        exportedActivity.Source.Name.ShouldBe(sourceName);
        exportedActivity.OperationName.ShouldBe(operationName);
        exportedActivity.Kind.ShouldBe(kind);
        exportedActivity.TraceId.ShouldBe(parent.TraceId);
        exportedActivity.ParentSpanId.ShouldBe(parent.SpanId);
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

        records.ShouldHaveSingleItem().ShouldBe(
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
