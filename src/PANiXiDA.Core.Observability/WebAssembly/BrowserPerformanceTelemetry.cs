using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace PANiXiDA.Core.Observability.WebAssembly;

internal sealed class BrowserPerformanceTelemetry(ILogger<BrowserPerformanceTelemetry> logger) : IDisposable
{
    internal const string SourceName = "PANiXiDA.Core.Observability.Browser";
    private readonly Meter _meter = new(SourceName);
    private readonly ActivitySource _activities = new(SourceName);
    private readonly ConcurrentDictionary<string, double> _vitals = new(StringComparer.Ordinal);
    private Histogram<double> _startup = null!;
    private Counter<double> _resourceCount = null!;
    private Counter<double> _resourceDuration = null!;
    private Counter<double> _transferSize = null!;
    private Counter<double> _decodedSize = null!;
    private DotNetObjectReference<BrowserPerformanceTelemetry>? _reference;
    private IJSInProcessRuntime? _runtime;

    internal void Start(IJSInProcessRuntime runtime)
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
        foreach (string name in new[] { "LCP", "INP", "CLS", "FCP", "TTFB" })
        {
            _meter.CreateObservableGauge<double>("browser.web_vital." + name.ToLowerInvariant(), () => ObserveVital(name), name == "CLS" ? "1" : "s");
        }

        _reference = DotNetObjectReference.Create(this);
        _runtime = runtime;
        try
        {
            runtime.InvokeVoid("panixidaObservability.start", _reference);
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

        var tags = new TagList { { "browser.wasm.phase", phase } };
        _startup.Record(duration / 1000, tags);
        var startTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(timeOrigin + started));
        using var activity = _activities.StartActivity("wasm.startup." + phase, ActivityKind.Internal,
            default(ActivityContext), tags, startTime: startTime);
        activity?.SetEndTime(startTime.AddMilliseconds(duration).UtcDateTime);
        logger.LogInformation("WASM {Phase} completed in {DurationMilliseconds} ms", phase, duration);
    }

    [JSInvokable]
    public void RecordResources(string kind, double count, double duration, double transferred, double decoded)
    {
        if (kind is not ("wasm" or "assembly" or "javascript" or "configuration" or "other")
            || !IsMeasurement(count) || !IsMeasurement(duration) || !IsMeasurement(transferred) || !IsMeasurement(decoded))
        {
            return;
        }

        var tags = new TagList { { "browser.resource.type", kind } };
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
        if (_vitals.TryGetValue(name, out double previous) && previous.Equals(measurement))
        {
            return;
        }

        _vitals[name] = measurement;
        logger.LogInformation("Web Vital {MetricName}: {Value} ({Rating}, {NavigationType})", name, measurement, rating, navigationType);
    }

    public void Dispose()
    {
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
        return _vitals.TryGetValue(name, out double value) ? [new Measurement<double>(value)] : [];
    }

    private static bool IsMeasurement(double value) => double.IsFinite(value) && value >= 0;
}
