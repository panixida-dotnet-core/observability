using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

using OpenTelemetry;

namespace PANiXiDA.Core.Observability.WebAssembly;

internal sealed class BrowserPerformanceTelemetry(ILogger<BrowserPerformanceTelemetry> logger) : IDisposable
{
    internal const string SourceName = "PANiXiDA.Core.Observability.Browser";
    private const string PageRouteAttribute = "page.route";
    private const string UnmatchedRoute = "[unmatched]";
    private readonly Meter _meter = new(SourceName);
    private readonly ActivitySource _activities = new(SourceName);
    private readonly ConcurrentDictionary<string, double> _vitals = new(StringComparer.Ordinal);
    private Histogram<double> _startup = null!;
    private Counter<double> _resourceCount = null!;
    private Counter<double> _resourceDuration = null!;
    private Counter<double> _transferSize = null!;
    private Counter<double> _decodedSize = null!;
    private Histogram<double> _navigationDuration = null!;
    private Histogram<double> _httpDuration = null!;
    private Histogram<double> _pageLoadDuration = null!;
    private PageLoad? _pageLoad;
    private MeterListener? _routes;
    private ActivityListener? _requests;
    private NavigationManager? _navigationManager;
    private string? _entryRoute;
    private string? _currentRoute;
    private long _navigationId;
    private long _navigationStarted;
    private PageNavigation? _pageNavigation;
    private DotNetObjectReference<BrowserPerformanceTelemetry>? _reference;
    private IJSInProcessRuntime? _runtime;

    internal void Start(IJSInProcessRuntime runtime, NavigationManager? navigationManager = null)
    {
        if (_reference is not null)
        {
            return;
        }

        _startup = _meter.CreateHistogram<double>("browser.wasm.startup.duration", "s");
        _resourceCount = _meter.CreateCounter<double>("browser.wasm.resource.count", "{resource}");
        _resourceDuration = _meter.CreateCounter<double>("browser.wasm.resource.duration", "s");
        _transferSize = _meter.CreateCounter<double>("browser.wasm.resource.transfer.size", "By");
        _decodedSize = _meter.CreateCounter<double>("browser.wasm.resource.decoded.size", "By");
        _navigationDuration = _meter.CreateHistogram<double>("browser.page.navigation.duration", "s");
        _httpDuration = _meter.CreateHistogram<double>("browser.page.http.duration", "s");
        _pageLoadDuration = _meter.CreateHistogram<double>("browser.page.load.duration", "s");
        foreach (string name in new[] { "LCP", "INP", "CLS", "FCP", "TTFB" })
        {
            _meter.CreateObservableGauge<double>("browser.web_vital." + name.ToLowerInvariant(), () => ObserveVital(name), name == "CLS" ? "1" : "s");
        }

        _reference = DotNetObjectReference.Create(this);
        _runtime = runtime;
        try
        {
            runtime.InvokeVoid("panixidaObservability.start", _reference);
            _navigationStarted = Stopwatch.GetTimestamp();
            _navigationManager = navigationManager;
            _navigationManager?.LocationChanged += OnLocationChanged;
            _routes = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "Microsoft.AspNetCore.Components"
                        && instrument.Name is "aspnetcore.components.navigate" or "aspnetcore.components.navigation")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _routes.SetMeasurementEventCallback<long>((_, _, tags, _) => OnRouteMatched(tags));
            _routes.Start();
            _requests = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "System.Net.Http",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => Sdk.SuppressInstrumentation
                    ? ActivitySamplingResult.None : ActivitySamplingResult.AllData,
                ActivityStarted = OnRequestStarted,
                ActivityStopped = OnRequestStopped
            };
            ActivitySource.AddActivityListener(_requests);
        }
        catch (JSException exception)
        {
            logger.LogWarning(exception, "Browser performance telemetry could not start");
        }
    }

    [JSInvokable]
    public void RecordStartup(string phase, double started, double duration, double timeOrigin)
    {
        if (phase is not ("runtime" or "document_to_runtime" or "first_frame")
            || !IsMeasurement(started) || !IsMeasurement(duration) || !IsMeasurement(timeOrigin)
            || timeOrigin + started + duration > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return;
        }

        var tags = new TagList { { "browser.wasm.phase", phase }, { PageRouteAttribute, _entryRoute ?? UnmatchedRoute } };
        _startup.Record(duration / 1000, tags);
        var startTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(timeOrigin + started));
        using var activity = _activities.StartActivity("wasm.startup." + phase, ActivityKind.Internal,
            default(ActivityContext), tags, startTime: startTime);
        activity?.SetEndTime(startTime.AddMilliseconds(duration).UtcDateTime);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("WASM {Phase} completed in {DurationMilliseconds} ms", phase, duration);
        }
    }

    [JSInvokable]
    public void RecordResources(string kind, double count, double duration, double transferred, double decoded)
    {
        if (kind is not ("wasm" or "assembly" or "javascript" or "configuration" or "other")
            || !IsMeasurement(count) || !IsMeasurement(duration) || !IsMeasurement(transferred) || !IsMeasurement(decoded))
        {
            return;
        }

        var tags = new TagList { { "browser.resource.type", kind }, { PageRouteAttribute, _entryRoute ?? UnmatchedRoute } };
        _resourceCount.Add(count, tags);
        _resourceDuration.Add(duration / 1000, tags);
        _transferSize.Add(transferred, tags);
        _decodedSize.Add(decoded, tags);
    }

    [JSInvokable]
    public void RecordVital(string name, double value, string rating, string navigationType)
    {
        if (name is not ("LCP" or "INP" or "CLS" or "FCP" or "TTFB") || !IsMeasurement(value))
        {
            return;
        }

        double measurement = name == "CLS" ? value : value / 1000;
        _vitals[name] = measurement;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Web Vital {MetricName}: {Value} ({Rating}, {NavigationType}), entry route {PageRoute}",
                name, measurement, rating, navigationType, _entryRoute ?? UnmatchedRoute);
        }
    }

    [JSInvokable]
    public void RecordNavigation(long navigationId, double frameDuration)
    {
        if (_pageNavigation is not { } page || page.Id != navigationId || !IsMeasurement(frameDuration))
        {
            return;
        }
        double duration = page.MatchDuration + frameDuration;
        if (duration > (DateTimeOffset.MaxValue - page.Started).TotalMilliseconds)
        {
            return;
        }
        _pageNavigation = null;
        TagList tags =
        [
            new(PageRouteAttribute, page.Route),
            new("browser.navigation.type", page.Id == 0 ? "initial" : "navigation")
        ];
        tags.Add("browser.navigation.phase", "route_match");
        _navigationDuration.Record(page.MatchDuration / 1000, tags);
        tags[^1] = new("browser.navigation.phase", "render_frame");
        _navigationDuration.Record(frameDuration / 1000, tags);
        tags[^1] = new("browser.navigation.phase", "first_frame");
        _navigationDuration.Record(duration / 1000, tags);
        tags.Add("page.previous.route", page.PreviousRoute);
        using var activity = _activities.StartActivity("browser.page.navigation", ActivityKind.Internal,
            page.Context, tags, startTime: page.Started);
        activity?.SetEndTime(page.Started.AddMilliseconds(duration).UtcDateTime);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Page navigation {PreviousRoute} -> {PageRoute} reached a rendering opportunity in {DurationMilliseconds} ms",
                page.PreviousRoute, page.Route, duration);
        }
    }

    public void Dispose()
    {
        CancelPageLoad();
        _navigationManager?.LocationChanged -= OnLocationChanged;
        _routes?.Dispose();
        _requests?.Dispose();
        try
        {
            _runtime?.InvokeVoid("panixidaObservability.stop");
        }
        catch (JSException exception)
        {
            logger.LogDebug(exception, "Browser performance telemetry could not disconnect");
        }
        _reference?.Dispose();
        _meter.Dispose();
        _activities.Dispose();
    }

    private Measurement<double>[] ObserveVital(string name)
    {
        return _vitals.TryGetValue(name, out double value)
            ? [new Measurement<double>(value, new KeyValuePair<string, object?>(PageRouteAttribute, _entryRoute ?? UnmatchedRoute))] : [];
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        CancelPageLoad();
        _navigationStarted = Stopwatch.GetTimestamp();
        _navigationId++;
        _pageNavigation = null;
        InvokeNavigation("panixidaObservability.navigationStarted", _navigationId);
    }

    private void OnRouteMatched(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key != "aspnetcore.components.route" || tag.Value is not string route || string.IsNullOrWhiteSpace(route))
            {
                continue;
            }
            string previous = _currentRoute ?? "[initial]";
            _entryRoute ??= route;
            _currentRoute = route;
            double matched = Stopwatch.GetElapsedTime(_navigationStarted).TotalMilliseconds;
            _pageNavigation = new(_navigationId, route, previous, matched,
                DateTimeOffset.UtcNow.AddMilliseconds(-matched), Activity.Current?.Context ?? default);
            _pageLoad = new(_pageNavigation);
            InvokeNavigation("panixidaObservability.routeChanged", _navigationId);
            return;
        }
    }

    private void OnRequestStarted(Activity activity)
    {
        if (Sdk.SuppressInstrumentation)
        {
            return;
        }
        string? route = null;
        for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.GetTagItem("aspnetcore.components.route") is string parentRoute)
            {
                route = parentRoute;
                break;
            }
        }
        route ??= _currentRoute;
        if (route is not null)
        {
            activity.SetTag(PageRouteAttribute, route);
        }
    }

    private void OnRequestStopped(Activity activity)
    {
        if (activity.GetTagItem(PageRouteAttribute) is string route)
        {
            _httpDuration.Record(activity.Duration.TotalSeconds, new KeyValuePair<string, object?>(PageRouteAttribute, route));
        }
    }

    private void InvokeNavigation(string identifier, long navigationId)
    {
        try
        {
            _runtime!.InvokeVoid(identifier, navigationId);
        }
        catch (JSException exception)
        {
            logger.LogDebug(exception, "Browser navigation telemetry could not update its route");
        }
    }

    internal void PageRendered(object observer, bool loading, bool failed)
    {
        if (_pageLoad is not { Completed: false } page || (page.Observer is not null && page.Observer != observer))
        {
            return;
        }
        page.Observer = observer;
        page.Version++;
        page.Outcome = null;
        if (loading && !failed)
        {
            return;
        }
        page.Outcome = failed ? "error" : "success";
        page.ReadyDuration = Stopwatch.GetElapsedTime(_navigationStarted).TotalMilliseconds;
        try
        {
            _runtime!.InvokeVoid("panixidaObservability.pageReady", page.Navigation.Id, page.Version);
        }
        catch (JSException exception)
        {
            logger.LogDebug(exception, "Browser page readiness could not be observed");
        }
    }

    internal void PageRemoved(object observer)
    {
        if (_pageLoad?.Observer == observer)
        {
            CancelPageLoad();
        }
    }

    [JSInvokable]
    public void RecordPageReady(long navigationId, long version, double frameDuration)
    {
        if (_pageLoad is not { Completed: false, Outcome: not null } page
            || page.Navigation.Id != navigationId || page.Version != version || !IsMeasurement(frameDuration))
        {
            return;
        }
        double duration = page.ReadyDuration + frameDuration;
        if (duration > (DateTimeOffset.MaxValue - page.Navigation.Started).TotalMilliseconds)
        {
            return;
        }
        CompletePageLoad(page, page.Outcome, duration);
    }

    private void CancelPageLoad()
    {
        if (_pageLoad is { Completed: false, Observer: not null } page)
        {
            CompletePageLoad(page, "cancelled", Stopwatch.GetElapsedTime(_navigationStarted).TotalMilliseconds);
        }
    }

    private void CompletePageLoad(PageLoad page, string outcome, double duration)
    {
        page.Completed = true;
        TagList tags =
        [
            new(PageRouteAttribute, page.Navigation.Route),
            new("browser.navigation.type", page.Navigation.Id == 0 ? "initial" : "navigation"),
            new("browser.page.load.outcome", outcome)
        ];
        _pageLoadDuration.Record(duration / 1000, tags);
        using var activity = _activities.StartActivity("browser.page.load", ActivityKind.Internal,
            page.Navigation.Context, tags, startTime: page.Navigation.Started);
        activity?.SetEndTime(page.Navigation.Started.AddMilliseconds(duration).UtcDateTime);
        if (outcome == "error")
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Page {PageRoute} load {Outcome} after {DurationMilliseconds} ms",
                page.Navigation.Route, outcome, duration);
        }
    }

    private sealed class PageLoad(PageNavigation navigation)
    {
        internal PageNavigation Navigation { get; } = navigation;
        internal object? Observer { get; set; }
        internal long Version { get; set; }
        internal string? Outcome { get; set; }
        internal double ReadyDuration { get; set; }
        internal bool Completed { get; set; }
    }

    private sealed record PageNavigation(long Id, string Route, string PreviousRoute, double MatchDuration,
        DateTimeOffset Started, ActivityContext Context);

    private static bool IsMeasurement(double value) => double.IsFinite(value) && value >= 0;
}
