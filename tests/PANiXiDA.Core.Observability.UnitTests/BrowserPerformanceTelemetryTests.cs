using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using PANiXiDA.Core.Observability.WebAssembly;

namespace PANiXiDA.Core.Observability.UnitTests;

[Collection(typeof(BrowserPerformanceTelemetryCollection))]
public sealed class BrowserPerformanceTelemetryTests
{
    [Fact(DisplayName = "Browser performance should export startup spans resources and latest Web Vitals through the configured providers")]
    public async Task BrowserPerformance_Should_ExportMeasurements_When_Connected()
    {
        var runtime = new TestRuntime();
        var builder = CreateBuilder(runtime);
        var activities = new ConcurrentBag<Activity>();
        var metrics = new ConcurrentDictionary<string, double>();
        var logs = new ConcurrentBag<string>();
        using var traceExporter = new RecordingExporter<Activity>(activities.Add);
        using var logExporter = new RecordingExporter<LogRecord>(record => logs.Add(record.FormattedMessage!));
        using var metricExporter = new RecordingExporter<Metric>(metric =>
        {
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                metrics[metric.Name] = metric.MetricType switch
                {
                    MetricType.Histogram => point.GetHistogramSum(),
                    MetricType.DoubleSum => point.GetSumDouble(),
                    _ => point.GetGaugeLastValueDouble()
                };
            }
        });
        using var reader = new BaseExportingMetricReader(metricExporter);
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(traceExporter)))
            .WithMetrics(meter => meter.AddReader(reader))
            .WithLogging(logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(logExporter)));
        await using var host = builder.Build();
        host.UseObservability();
        host.UseObservability();
        runtime.Starts.ShouldBe(1);
        var collector = runtime.Reference.ShouldNotBeNull().Value;

        reader.Collect().ShouldBeTrue();
        metrics.Keys.ShouldNotContain("browser.web_vital.lcp");
        collector.RecordStartup("runtime", 100, 200, 1700000000000);
        collector.RecordStartup("document_to_runtime", 0, 300, 1700000000000);
        collector.RecordStartup("first_frame", 300, 16, 1700000000000);
        foreach (string kind in new[] { "wasm", "assembly", "javascript", "configuration", "other" })
        {
            collector.RecordResources(kind, 2, 150, 1000, 2000);
        }
        foreach (string name in new[] { "LCP", "INP", "FCP", "TTFB" })
        {
            collector.RecordVital(name, 100, "good", "navigate");
        }
        collector.RecordVital("CLS", 0.05, "good", "navigate");
        collector.RecordVital("LCP", 100, "good", "navigate");
        collector.RecordVital("LCP", 200, "good", "navigate");

        reader.Collect().ShouldBeTrue();

        metrics["browser.web_vital.lcp"].ShouldBe(0.2);
        metrics["browser.web_vital.inp"].ShouldBe(0.1);
        metrics["browser.web_vital.fcp"].ShouldBe(0.1);
        metrics["browser.web_vital.ttfb"].ShouldBe(0.1);
        metrics["browser.web_vital.cls"].ShouldBe(0.05);
        metrics["browser.wasm.resource.count"].ShouldBe(2);
        metrics["browser.wasm.resource.duration"].ShouldBe(0.15);
        metrics["browser.wasm.resource.transfer.size"].ShouldBe(1000);
        metrics["browser.wasm.resource.decoded.size"].ShouldBe(2000);
        activities.Count.ShouldBe(3);
        var runtimeActivity = activities.Single(activity => activity.OperationName == "wasm.startup.runtime");
        runtimeActivity.StartTimeUtc.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1700000000100).UtcDateTime);
        runtimeActivity.Duration.ShouldBe(TimeSpan.FromMilliseconds(200));
        logs.Count(message => message.StartsWith("Web Vital LCP", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact(DisplayName = "Browser performance should ignore malformed and unsupported measurements")]
    public void BrowserPerformance_Should_IgnoreInvalidMeasurements()
    {
        using var factory = LoggerFactory.Create(_ => { });
        using var unstarted = new BrowserPerformanceTelemetry(factory.CreateLogger<BrowserPerformanceTelemetry>());
        using var collector = new BrowserPerformanceTelemetry(factory.CreateLogger<BrowserPerformanceTelemetry>());
        collector.RecordStartup("unknown", 0, 1, 1);
        collector.RecordResources("unknown", 1, 1, 1, 1);
        collector.RecordVital("unknown", 1, "good", "navigate");
        foreach (double value in new[] { -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            collector.RecordStartup("runtime", value, 1, 1);
            collector.RecordStartup("runtime", 1, value, 1);
            collector.RecordStartup("runtime", 1, 1, value);
            collector.RecordResources("wasm", value, 1, 1, 1);
            collector.RecordResources("wasm", 1, value, 1, 1);
            collector.RecordResources("wasm", 1, 1, value, 1);
            collector.RecordResources("wasm", 1, 1, 1, value);
            collector.RecordVital("LCP", value, "good", "navigate");
        }
        collector.RecordStartup("runtime", double.MaxValue, 1, 1);
        collector.Start(new TestRuntime());
        collector.RecordStartup("runtime", 0, 0, 1700000000000);
    }

    [Fact(DisplayName = "Browser performance should respect its opt out without invoking JavaScript")]
    public async Task BrowserPerformance_Should_StayDisabled_When_Configured()
    {
        var runtime = new TestRuntime();
        var builder = CreateBuilder(runtime, enabled: false);
        await using var host = builder.Build();

        host.UseObservability();

        host.Services.GetService<BrowserPerformanceTelemetry>().ShouldBeNull();
        runtime.Starts.ShouldBe(0);
    }

    [Theory(DisplayName = "Browser performance JavaScript failures should not prevent the application from starting or stopping")]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BrowserPerformance_Should_TolerateJavaScriptFailure(bool failStart, bool failStop)
    {
        var runtime = new TestRuntime { FailStart = failStart, FailStop = failStop };
        var builder = CreateBuilder(runtime);
        await using var host = builder.Build();

        host.UseObservability();
        host.Services.GetRequiredService<BrowserPerformanceTelemetry>().Dispose();

        runtime.Starts.ShouldBe(1);
    }

    private static Microsoft.AspNetCore.Components.WebAssembly.Hosting.WebAssemblyHostBuilder CreateBuilder(TestRuntime runtime, bool enabled = true)
    {
        var builder = WebAssemblyTestHost.CreateBuilder("https://admin.example.test/");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "browser-performance-test",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "/telemetry",
            ["BrowserTelemetry:Performance:Enabled"] = enabled.ToString()
        });
        builder.Services.AddSingleton<IJSRuntime>(runtime);
        builder.AddObservability();
        return builder;
    }

    private sealed class TestRuntime : IJSInProcessRuntime
    {
        public int Starts { get; private set; }
        public bool FailStart { get; init; }
        public bool FailStop { get; init; }
        public DotNetObjectReference<BrowserPerformanceTelemetry>? Reference { get; private set; }

        public TValue Invoke<TValue>(string identifier, params object?[]? args)
        {
            if (identifier == "panixidaObservability.start")
            {
                Starts++;
                Reference = (DotNetObjectReference<BrowserPerformanceTelemetry>)args![0]!;
                if (FailStart) throw new JSException("start unavailable");
            }
            else if (FailStop)
            {
                throw new JSException("stop unavailable");
            }
            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }

    private sealed class RecordingExporter<T>(Action<T> record) : BaseExporter<T> where T : class
    {
        public override ExportResult Export(in Batch<T> batch)
        {
            foreach (var item in batch) record(item);
            return ExportResult.Success;
        }
    }
}

[CollectionDefinition(DisableParallelization = true)]
public sealed class BrowserPerformanceTelemetryCollection;
