using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.Http;

namespace PANiXiDA.Core.Observability.WebAssembly;

internal sealed class BrowserHttpTelemetryFilter(Uri endpoint) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        if (builder.Name?.StartsWith("Otlp", StringComparison.Ordinal) != true)
        {
            builder.AdditionalHandlers.Insert(0, new BrowserHttpTelemetryHandler(endpoint));
        }
    };

    private sealed class BrowserHttpTelemetryHandler(Uri endpoint) : DelegatingHandler
    {
        private static readonly ActivitySource Source = new("PANiXiDA.Core.Observability.WebAssembly.Http");
        private static readonly Meter Meter = new("PANiXiDA.Core.Observability.WebAssembly.Http");
        private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("http.client.request.duration", "s");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var activity = Source.StartActivity("HTTP " + request.Method.Method, ActivityKind.Client);
            activity?.SetTag("http.request.method", request.Method.Method);
            activity?.SetTag("server.address", request.RequestUri?.Host);
            activity?.SetTag("url.path", request.RequestUri?.AbsolutePath);
            if (activity is not null && request.RequestUri?.GetLeftPart(UriPartial.Authority) == endpoint.GetLeftPart(UriPartial.Authority))
            {
                request.Headers.Remove("traceparent");
                request.Headers.TryAddWithoutValidation("traceparent", $"00-{activity.TraceId}-{activity.SpanId}-{((byte)activity.ActivityTraceFlags & 1):x2}");
                if (!string.IsNullOrEmpty(activity.TraceStateString))
                {
                    request.Headers.Remove("tracestate");
                    request.Headers.TryAddWithoutValidation("tracestate", activity.TraceStateString);
                }
            }

            long started = Stopwatch.GetTimestamp();
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                activity?.SetTag("http.response.status_code", (int)response.StatusCode);
                return response;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                activity?.SetTag("error.type", exception.GetType().FullName);
                activity?.SetStatus(ActivityStatusCode.Error);
                throw;
            }
            finally
            {
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
                    new KeyValuePair<string, object?>("http.request.method", request.Method.Method));
            }
        }
    }
}
