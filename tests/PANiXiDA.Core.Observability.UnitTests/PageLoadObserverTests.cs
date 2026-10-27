using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using PANiXiDA.Core.Observability.WebAssembly;

#pragma warning disable BL0006

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed partial class BrowserPerformanceTelemetryTests
{
    [Fact(DisplayName = "Page readiness should wait for data and rendering, report each outcome once, and reject stale callbacks")]
    public async Task PageLoad_Should_WaitForDataAndRenderAndKeepOutcomes()
    {
        var runtime = new TestRuntime();
        var navigation = new TestNavigationManager();
        var builder = CreateBuilder(runtime);
        builder.Services.AddSingleton<NavigationManager>(navigation);
        var outcomes = new ConcurrentDictionary<string, long>();
        var durations = new ConcurrentDictionary<string, double>();
        var activities = new ConcurrentBag<Activity>();
        using var exporter = new RecordingExporter<Metric>(metric =>
        {
            if (metric.Name != "browser.page.load.duration") return;
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, object?>();
                foreach (var tag in point.Tags) tags[tag.Key] = tag.Value;
                tags["page.route"].ShouldBe("/computers/{Id:int}");
                string outcome = (string)tags["browser.page.load.outcome"]!;
                outcomes[outcome] = point.GetHistogramCount();
                durations[outcome] = point.GetHistogramSum();
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
        using var renderer = new PageObserverRenderer(host.Services);
        int observer = renderer.Mount();
        var otherObserver = new object();

        collector.PageRendered(otherObserver, false, false);
        collector.PageRemoved(otherObserver);
        collector.RecordPageReady(0, 1, 1);
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/computers/{Id:int}"));
        await renderer.RenderAsync(observer, true, false);
        collector.RecordPageReady(0, 1, 1);
        reader.Collect().ShouldBeTrue();
        outcomes.ShouldBeEmpty();
        await renderer.RenderAsync(observer, false, false);
        collector.PageRendered(otherObserver, false, false);
        collector.RecordPageReady(1, 2, 16);
        collector.RecordPageReady(0, 1, 16);
        collector.RecordPageReady(0, 2, -1);
        collector.RecordPageReady(0, 2, double.NaN);
        collector.RecordPageReady(0, 2, double.MaxValue);
        collector.RecordPageReady(0, 2, 16);
        collector.RecordPageReady(0, 2, 16);
        await renderer.RenderAsync(observer, false, false);
        collector.PageRemoved(otherObserver);

        navigation.NavigateTo("https://admin.example.test/computers/2");
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/computers/{Id:int}"));
        await renderer.RenderAsync(observer, true, true);
        collector.RecordPageReady(1, 1, 16);

        navigation.NavigateTo("https://admin.example.test/computers/3");
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/computers/{Id:int}"));
        await renderer.RenderAsync(observer, false, false);
        await renderer.RenderAsync(observer, true, false);
        collector.RecordPageReady(2, 1, 16);
        reader.Collect().ShouldBeTrue();
        outcomes.ShouldNotContainKey("cancelled");
        navigation.NavigateTo("https://admin.example.test/computers/4");
        collector.RecordPageReady(2, 2, 16);
        matched.Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/computers/{Id:int}"));
        await renderer.RenderAsync(observer, true, false);
        renderer.Dispose();
        collector.Dispose();
        reader.Collect().ShouldBeTrue();

        outcomes["success"].ShouldBe(1);
        outcomes["error"].ShouldBe(1);
        outcomes["cancelled"].ShouldBe(2);
        durations["success"].ShouldBeGreaterThanOrEqualTo(0.016);
        var loads = activities.Where(activity => activity.OperationName == "browser.page.load").ToArray();
        loads.Length.ShouldBe(4);
        loads.Single(activity => activity.GetTagItem("browser.page.load.outcome") as string == "error")
            .Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Theory(DisplayName = "Page observer should be harmless without browser performance telemetry")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PageLoad_Should_BeOptional(bool browserHost)
    {
        await using var host = CreateBuilder(new TestRuntime(), enabled: false).Build();
        using var server = new ServiceCollection().BuildServiceProvider();
        using var renderer = new PageObserverRenderer(browserHost ? host.Services : server);
        int observer = renderer.Mount();
        await renderer.RenderAsync(observer, false, true);
        renderer.Dispose();
    }

    [Fact(DisplayName = "Readiness interop failure should not mark a page as successful")]
    public async Task PageLoad_Should_TolerateInteropFailureAndDisabledLogging()
    {
        var runtime = new TestRuntime { FailRoute = true };
        using var collector = new BrowserPerformanceTelemetry(NullLogger<BrowserPerformanceTelemetry>.Instance);
        collector.Start(runtime);
        using var routes = new Meter("Microsoft.AspNetCore.Components");
        routes.CreateCounter<long>("aspnetcore.components.navigate")
            .Add(1, new KeyValuePair<string, object?>("aspnetcore.components.route", "/"));
        var observer = new object();
        collector.PageRendered(observer, false, false);
        collector.PageRemoved(observer);
        collector.PageRemoved(observer);
        await Task.CompletedTask;
    }

    private sealed class PageObserverRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Mount() => AssignRootComponentId(InstantiateComponent(typeof(PageLoadObserver)));
        public Task RenderAsync(int id, bool loading, bool error) => Dispatcher.InvokeAsync(() =>
            RenderRootComponentAsync(id, ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PageLoadObserver.IsLoading)] = loading,
                [nameof(PageLoadObserver.HasError)] = error
            })));
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
}
