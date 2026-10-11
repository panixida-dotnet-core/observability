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

public sealed class WolverineTelemetryTests
{
    [Theory(DisplayName = "AddObservability collects application-specific Wolverine meters")]
    [InlineData("Wolverine:Orders")]
    [InlineData("Wolverine:Notifications")]
    public void AddObservability_ShouldCollectWolverineMetrics(string meterName)
    {
        var measurements = new ConcurrentBag<(string Meter, long Value)>();
        using var exporter = new RecordingExporter<Metric>(metric =>
        {
            if (metric.Name != "wolverine-messages-succeeded")
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

        meter.CreateCounter<long>("wolverine-messages-succeeded").Add(3);
        unrelatedMeter.CreateCounter<long>("wolverine-messages-succeeded").Add(7);
        reader.Collect().Should().BeTrue();

        measurements.Should().ContainSingle().Which.Should().Be((meterName, 3L));
    }

    [Fact(DisplayName = "AddObservability collects Wolverine spans with their parent context")]
    public void AddObservability_ShouldCollectWolverineTraces()
    {
        var activities = new ConcurrentBag<Activity>();
        using var exporter = new RecordingExporter<Activity>(activities.Add);
        using var processor = new SimpleActivityExportProcessor(exporter);
        var builder = CreateBuilder();
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(processor));

        using var app = builder.Build();
        _ = app.Services.GetRequiredService<TracerProvider>();
        using var source = new ActivitySource("Wolverine");
        var parent = new ActivityContext(
            ActivityTraceId.CreateRandom(),
            ActivitySpanId.CreateRandom(),
            ActivityTraceFlags.Recorded);

        using (var activity = source.StartActivity("message processing", ActivityKind.Consumer, parent))
        {
            activity.Should().NotBeNull();
        }

        var exportedActivity = activities.Should().ContainSingle().Subject;
        exportedActivity.Source.Name.Should().Be("Wolverine");
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
            ApplicationName = typeof(WolverineTelemetryTests).Assembly.FullName,
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
