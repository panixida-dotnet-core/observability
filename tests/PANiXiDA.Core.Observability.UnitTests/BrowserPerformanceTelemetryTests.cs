using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Components;
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
public sealed partial class BrowserPerformanceTelemetryTests
{
    [Theory(DisplayName = "Browser duration histograms should distinguish subsecond measurements")]
    [InlineData("browser.page.navigation.duration")]
    [InlineData("browser.page.http.duration")]
    [InlineData("browser.page.load.duration")]
    [InlineData("browser.wasm.startup.duration")]
    public async Task BrowserPerformance_Should_UseSecondScaleHistogramBuckets(string name)
    {
        var builder = CreateBuilder(new TestRuntime());
        var buckets = new ConcurrentDictionary<double, long>();
        using var exporter = new RecordingExporter<Metric>(metric =>
        {
            if (metric.Name != name) return;
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                foreach (var bucket in point.GetHistogramBuckets())
                {
                    buckets[bucket.ExplicitBound] = bucket.BucketCount;
                }
            }
        });
        using var reader = new BaseExportingMetricReader(exporter);
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(reader));
        await using var host = builder.Build();
        host.UseObservability();
        using var meter = new Meter(BrowserPerformanceTelemetry.SourceName);
        meter.CreateHistogram<double>(name, "s").Record(0.2);

        reader.Collect().ShouldBeTrue();

        buckets.ShouldContainKey(0.005);
        buckets[0.1].ShouldBe(0);
        buckets[0.25].ShouldBe(1);
        buckets[double.PositiveInfinity].ShouldBe(0);
    }

    [Fact(DisplayName = "Browser page metrics should discover route templates and preserve document and request attribution across navigations")]
    public async Task BrowserPerformance_Should_AttributePageMetricsToFrameworkRoutes()
    {
        var runtime = new TestRuntime();
        var navigation = new TestNavigationManager();
        var builder = CreateBuilder(runtime);
        builder.Services.AddSingleton<NavigationManager>(navigation);
        var measurements = new ConcurrentDictionary<(string Metric, string Route, string Phase), double>();
        var activities = new ConcurrentBag<Activity>();
        using var exporter = new RecordingExporter<Metric>(metric =>
        {
            if (!metric.Name.StartsWith("browser.", StringComparison.Ordinal)) return;
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, object?>();
                foreach (var tag in point.Tags) tags[tag.Key] = tag.Value;
                string route = tags.GetValueOrDefault("page.route") as string ?? "";
                string phase = tags.GetValueOrDefault("browser.navigation.phase") as string ?? "";
                measurements[(metric.Name, route, phase)] = metric.MetricType == MetricType.Histogram
                    ? point.GetHistogramSum() : point.GetGaugeLastValueDouble();
            }
        });
        using var reader = new BaseExportingMetricReader(exporter);
        using var traceExporter = new RecordingExporter<Activity>(activities.Add);
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddReader(reader))
            .WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(traceExporter)));
        await using var host = builder.Build();
        host.UseObservability();
        var collector = runtime.Reference.ShouldNotBeNull().Value;
        using var routes = new Meter("Microsoft.AspNetCore.Components");
        var matched = routes.CreateCounter<long>("aspnetcore.components.navigate");
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/units"));
        collector.RecordNavigation(0, 16);
        collector.RecordNavigation(0, 16);
        collector.RecordVital("LCP", 200, "good", "navigate");
        using var http = new ActivitySource("System.Net.Http");
        var oldRequest = http.StartActivity("System.Net.Http.HttpRequestOut").ShouldNotBeNull();
        navigation.NavigateTo("https://admin.example.test/computers/123?secret=value");
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/computers/{id:int}"));
        collector.RecordNavigation(0, 20);
        collector.RecordNavigation(1, -1);
        collector.RecordNavigation(1, double.NaN);
        collector.RecordNavigation(1, double.MaxValue);
        collector.RecordNavigation(1, 32);
        oldRequest.Stop();
        using (var request = http.StartActivity("System.Net.Http.HttpRequestOut"))
        {
            request.ShouldNotBeNull().GetTagItem("page.route").ShouldBe("/computers/{id:int}");
        }
        collector.RecordVital("INP", 50, "good", "navigate");
        reader.Collect().ShouldBeTrue();

        measurements[("browser.web_vital.lcp", "/units", "")].ShouldBe(0.2);
        measurements[("browser.web_vital.inp", "/units", "")].ShouldBe(0.05);
        measurements[("browser.page.navigation.duration", "/units", "first_frame")].ShouldBeGreaterThanOrEqualTo(0.016);
        measurements[("browser.page.navigation.duration", "/computers/{id:int}", "render_frame")].ShouldBe(0.032);
        measurements[("browser.page.http.duration", "/units", "")].ShouldBeGreaterThanOrEqualTo(0);
        measurements[("browser.page.http.duration", "/computers/{id:int}", "")].ShouldBeGreaterThanOrEqualTo(0);
        activities.Count(activity => activity.OperationName == "browser.page.navigation").ShouldBe(2);
        measurements.Keys.ShouldNotContain(key => key.Route.Contains("123", StringComparison.Ordinal) || key.Route.Contains("secret", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Browser page metrics should retain parent route attribution and ignore telemetry export requests")]
    public async Task BrowserPerformance_Should_IgnoreSuppressedRequestsAndUseParentRoutes()
    {
        var runtime = new TestRuntime();
        var builder = CreateBuilder(runtime);
        await using var host = builder.Build();
        host.UseObservability();
        var collector = runtime.Reference.ShouldNotBeNull().Value;
        using var http = new ActivitySource("System.Net.Http");
        using (var noPage = http.StartActivity("System.Net.Http.HttpRequestOut"))
        {
            noPage.ShouldNotBeNull().GetTagItem("page.route").ShouldBeNull();
        }
        using (SuppressInstrumentationScope.Begin())
        {
            http.StartActivity("export").ShouldBeNull();
            using var otherListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "System.Net.Http",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
            };
            ActivitySource.AddActivityListener(otherListener);
            using var suppressed = http.StartActivity("export observed by another subscriber");
            suppressed.ShouldNotBeNull().GetTagItem("page.route").ShouldBeNull();
        }
        using var routes = new Meter("Microsoft.AspNetCore.Components");
        var matched = routes.CreateCounter<long>("aspnetcore.components.navigation");
        routes.CreateCounter<long>("unrelated").Add(1);
        matched.Add(1, new KeyValuePair<string, object?>("other", "/private/123"));
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", 123));
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", " "));
        collector.RecordNavigation(0, 1);
        using var parent = new Activity("older page").SetTag("aspnetcore.components.route", "/older/{id}").Start();
        using var intermediate = new Activity("work").Start();
        using var request = http.StartActivity("System.Net.Http.HttpRequestOut");
        request.ShouldNotBeNull().GetTagItem("page.route").ShouldBe("/older/{id}");
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/newer"));
        collector.RecordNavigation(0, 1);
    }

    [Fact(DisplayName = "Browser page telemetry should tolerate JavaScript route update failures")]
    public async Task BrowserPerformance_Should_TolerateRouteUpdateFailure()
    {
        var runtime = new TestRuntime { FailRoute = true };
        var builder = CreateBuilder(runtime);
        var navigation = new TestNavigationManager();
        builder.Services.AddSingleton<NavigationManager>(navigation);
        await using var host = builder.Build();
        host.UseObservability();
        Should.NotThrow(() => navigation.NavigateTo("https://admin.example.test/units"));
        using var routes = new Meter("Microsoft.AspNetCore.Components");
        var matched = routes.CreateCounter<long>("aspnetcore.components.navigate");
        Should.NotThrow(() => matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/units")));
    }

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
                    MetricType.LongSum or MetricType.LongSumNonMonotonic => point.GetSumLong(),
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
        logs.Count(message => message.StartsWith("Web Vital LCP", StringComparison.Ordinal)).ShouldBe(3);
    }

    [Fact(DisplayName = "Browser performance should ignore malformed and unsupported measurements")]
    public void BrowserPerformance_Should_IgnoreInvalidMeasurements()
    {
        using var factory = LoggerFactory.Create(_ => { });
        using var unstarted = new BrowserPerformanceTelemetry(factory.CreateLogger<BrowserPerformanceTelemetry>());
        using var collector = new BrowserPerformanceTelemetry(factory.CreateLogger<BrowserPerformanceTelemetry>());
        Should.NotThrow(() => collector.RecordStartup("unknown", 0, 1, 1));
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
        collector.RecordVital("LCP", 100, "good", "navigate");
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
        public bool FailRoute { get; init; }
        public DotNetObjectReference<BrowserPerformanceTelemetry>? Reference { get; private set; }

        public TValue Invoke<TValue>(string identifier, params object?[]? args)
        {
            if (identifier == "panixidaObservability.start")
            {
                Starts++;
                Reference = (DotNetObjectReference<BrowserPerformanceTelemetry>)args![0]!;
                if (FailStart) throw new JSException("start unavailable");
            }
            else if (FailStop && identifier == "panixidaObservability.stop")
            {
                throw new JSException("stop unavailable");
            }
            else if (FailRoute)
            {
                throw new JSException("route update unavailable");
            }
            return default!;
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://admin.example.test/", "https://admin.example.test/units");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = uri;
            NotifyLocationChanged(false);
        }
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
