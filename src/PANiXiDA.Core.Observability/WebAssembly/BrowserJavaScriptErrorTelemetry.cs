using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

using PANiXiDA.Core.Observability.Options.BrowserObservability;

namespace PANiXiDA.Core.Observability.WebAssembly;

internal sealed partial class BrowserJavaScriptErrorTelemetry(
    ILogger<BrowserJavaScriptErrorTelemetry> logger,
    IOptions<BrowserObservabilityOptions> options,
    TimeProvider clock) : IDisposable
{
    private readonly Meter _meter = new(BrowserPerformanceTelemetry.SourceName);
    private readonly ActivitySource _activities = new(BrowserPerformanceTelemetry.SourceName);
    private Counter<long> _errors = null!;
    private MeterListener? _routes;
    private string _route = "[unmatched]";
    private IJSInProcessRuntime? _runtime;
    private DotNetObjectReference<BrowserJavaScriptErrorTelemetry>? _reference;
    private long _window;
    private int _reported;

    internal void Start(IJSInProcessRuntime runtime)
    {
        if (_reference is not null)
        {
            return;
        }
        _errors = _meter.CreateCounter<long>("browser.javascript.errors", "{error}");
        _window = clock.GetTimestamp();
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
        _routes.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "aspnetcore.components.route" && tag.Value is string route && !string.IsNullOrWhiteSpace(route))
                {
                    _route = route;
                }
            }
        });
        _routes.Start();
        _runtime = runtime;
        _reference = DotNetObjectReference.Create(this);
        try
        {
            runtime.InvokeVoid("panixidaObservability.startErrors", _reference, options.Value.JavaScriptErrorsPerMinute);
        }
        catch (JSException exception)
        {
            logger.LogWarning(exception, "Browser JavaScript error telemetry could not start");
        }
    }

    [JSInvokable]
    public void RecordError(string kind, string message, string? stack)
    {
        if (kind is not ("error" or "unhandledrejection") || string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        if (clock.GetElapsedTime(_window) >= TimeSpan.FromMinutes(1))
        {
            _window = clock.GetTimestamp();
            _reported = 0;
        }
        if (_reported >= options.Value.JavaScriptErrorsPerMinute)
        {
            return;
        }
        _reported++;
        var tags = new TagList { { "page.route", _route }, { "browser.error.kind", kind } };
        _errors.Add(1, tags);
        using var activity = _activities.StartActivity("browser.javascript.error", ActivityKind.Internal, default(ActivityContext), tags);
        activity?.SetStatus(ActivityStatusCode.Error);
        logger.LogError("Browser JavaScript {ErrorKind} on {PageRoute}: {ErrorMessage}; stack: {JavaScriptStack}",
            kind, _route, Sanitize(message, 2048), Sanitize(stack ?? "", 8192));
    }

    public void Dispose()
    {
        _routes?.Dispose();
        try
        {
            _runtime?.InvokeVoid("panixidaObservability.stopErrors");
        }
        catch (JSException exception)
        {
            logger.LogDebug(exception, "Browser JavaScript error telemetry could not disconnect");
        }
        _reference?.Dispose();
        _activities.Dispose();
        _meter.Dispose();
    }

    private static string Sanitize(string value, int limit)
    {
        string bounded = value.Length > limit ? value[..limit] : value;
        return UrlQuery().Replace(bounded, "$1[redacted]");
    }

    [GeneratedRegex(@"(https?://[^\s?#]+)[?#][^\s]*", RegexOptions.CultureInvariant)]
    private static partial Regex UrlQuery();
}
