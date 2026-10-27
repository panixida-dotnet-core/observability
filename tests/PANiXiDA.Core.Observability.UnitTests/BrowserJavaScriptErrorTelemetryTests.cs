using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using PANiXiDA.Core.Observability.Options.BrowserObservability;
using PANiXiDA.Core.Observability.WebAssembly;

namespace PANiXiDA.Core.Observability.UnitTests;

[Collection(typeof(BrowserPerformanceTelemetryCollection))]
public sealed class BrowserJavaScriptErrorTelemetryTests
{
    [Fact(DisplayName = "Global JavaScript errors should export bounded logs, error spans and low-cardinality route metrics")]
    public async Task JavaScriptErrors_Should_ExportBoundedSignalsWithRouteTemplates()
    {
        var runtime = new ErrorRuntime();
        var clock = new TestClock();
        var builder = WebAssemblyTestHost.CreateBuilder("https://admin.example.test/");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "browser-errors-test",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "/telemetry",
            ["BrowserTelemetry:Performance:Enabled"] = "false",
            ["BrowserTelemetry:JavaScriptErrors:MaxPerMinute"] = "2"
        });
        builder.Services.AddSingleton<IJSRuntime>(runtime);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.AddObservability();
        var logs = new ConcurrentBag<string>();
        var activities = new ConcurrentBag<Activity>();
        var measurements = new ConcurrentDictionary<(string Route, string Kind), long>();
        using var logExporter = new ErrorExporter<LogRecord>(log => logs.Add(log.FormattedMessage ?? ""));
        using var traceExporter = new ErrorExporter<Activity>(activities.Add);
        using var metricExporter = new ErrorExporter<Metric>(metric =>
        {
            if (metric.Name != "browser.javascript.errors") return;
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, object?>();
                foreach (var tag in point.Tags) tags[tag.Key] = tag.Value;
                tags.Count.ShouldBe(2);
                measurements[((string)tags["page.route"]!, (string)tags["browser.error.kind"]!)] = point.GetSumLong();
            }
        });
        using var reader = new BaseExportingMetricReader(metricExporter);
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddReader(reader))
            .WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(traceExporter)))
            .WithLogging(logging => logging.AddProcessor(new SimpleLogRecordExportProcessor(logExporter)));
        await using var host = builder.Build();
        host.UseObservability();
        host.UseObservability();
        runtime.Starts.ShouldBe(1);
        var collector = runtime.Reference.ShouldNotBeNull().Value;
        collector.RecordError("unknown", "ignored", null);
        collector.RecordError("error", " ", null);
        collector.RecordError("error", "plain failure", null);
        using var routes = new Meter("Microsoft.AspNetCore.Components");
        routes.CreateCounter<long>("unrelated").Add(1);
        var matched = routes.CreateCounter<long>("aspnetcore.components.navigation");
        matched.Add(1, new KeyValuePair<string, object?>("unrelated", "/secret/12"));
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", 12));
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", " "));
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/computers/{Id:int}"));
        collector.RecordError("unhandledrejection", "failed https://api.test/path?token=secret#private", "at https://app.test/script.js?auth=secret:2:1");
        collector.RecordError("error", "throttled", "");
        clock.Advance(TimeSpan.FromMinutes(1));
        collector.RecordError("error", new string('m', 3000), new string('s', 10000));
        reader.Collect().ShouldBeTrue();

        measurements[("[unmatched]", "error")].ShouldBe(1);
        measurements[("/computers/{Id:int}", "unhandledrejection")].ShouldBe(1);
        measurements[("/computers/{Id:int}", "error")].ShouldBe(1);
        activities.Count.ShouldBe(3);
        activities.ShouldAllBe(activity => activity.Status == ActivityStatusCode.Error
            && activity.OperationName == "browser.javascript.error");
        logs.ShouldContain(message => message.Contains("[redacted]", StringComparison.Ordinal));
        logs.ShouldNotContain(message => message.Contains("secret", StringComparison.Ordinal)
            || message.Contains("private", StringComparison.Ordinal)
            || message.Contains("throttled", StringComparison.Ordinal)
            || message.Contains(new string('m', 2049), StringComparison.Ordinal)
            || message.Contains(new string('s', 8193), StringComparison.Ordinal));
    }

    [Theory(DisplayName = "JavaScript error telemetry should tolerate unavailable browser interop")]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void JavaScriptErrors_Should_TolerateInteropFailure(bool failStart, bool failStop)
    {
        using var unstarted = new BrowserJavaScriptErrorTelemetry(NullLogger<BrowserJavaScriptErrorTelemetry>.Instance,
            Microsoft.Extensions.Options.Options.Create(new BrowserObservabilityOptions()), TimeProvider.System);
        using var collector = new BrowserJavaScriptErrorTelemetry(NullLogger<BrowserJavaScriptErrorTelemetry>.Instance,
            Microsoft.Extensions.Options.Options.Create(new BrowserObservabilityOptions()), TimeProvider.System);
        Should.NotThrow(() => collector.Start(new ErrorRuntime { FailStart = failStart, FailStop = failStop }));
        collector.RecordError("error", "failure", "");
    }

    [Fact(DisplayName = "JavaScript errors should have an independent opt out")]
    public async Task JavaScriptErrors_Should_RespectOptOut()
    {
        var builder = WebAssemblyTestHost.CreateBuilder("https://admin.example.test/");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "browser-errors-test",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "/telemetry",
            ["BrowserTelemetry:JavaScriptErrors:Enabled"] = "false"
        });
        builder.Services.AddSingleton<IJSRuntime>(new ErrorRuntime());
        builder.AddObservability();
        await using var host = builder.Build();
        host.UseObservability();
        host.Services.GetService<BrowserJavaScriptErrorTelemetry>().ShouldBeNull();
        host.Services.GetService<BrowserPerformanceTelemetry>().ShouldNotBeNull();
    }

    [Theory(DisplayName = "JavaScript error limit should be validated only while enabled")]
    [InlineData(true, 0, true)]
    [InlineData(true, -1, true)]
    [InlineData(true, 1001, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 1000, false)]
    [InlineData(false, 0, false)]
    public void JavaScriptErrors_Should_ValidateLimit(bool enabled, int limit, bool failed)
    {
        var result = new BrowserObservabilityOptionsValidator().Validate(null, new BrowserObservabilityOptions
        {
            ServiceName = "browser",
            Endpoint = "/telemetry",
            JavaScriptErrorsEnabled = enabled,
            JavaScriptErrorsPerMinute = limit
        });
        result.Failed.ShouldBe(failed);
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class ErrorRuntime : IJSInProcessRuntime
    {
        public bool FailStart { get; init; }
        public bool FailStop { get; init; }
        public int Starts { get; private set; }
        public DotNetObjectReference<BrowserJavaScriptErrorTelemetry>? Reference { get; private set; }
        public TValue Invoke<TValue>(string identifier, params object?[]? args)
        {
            if (identifier == "panixidaObservability.startErrors")
            {
                Starts++;
                Reference = (DotNetObjectReference<BrowserJavaScriptErrorTelemetry>)args![0]!;
                if (FailStart) throw new JSException("start unavailable");
            }
            if (identifier == "panixidaObservability.stopErrors" && FailStop) throw new JSException("stop unavailable");
            return default!;
        }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }

    private sealed class ErrorExporter<T>(Action<T> record) : BaseExporter<T> where T : class
    {
        public override ExportResult Export(in Batch<T> batch)
        {
            foreach (var item in batch) record(item);
            return ExportResult.Success;
        }
    }
}
