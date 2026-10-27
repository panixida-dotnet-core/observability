using System.Diagnostics.Metrics;

using Yarp.ReverseProxy.Forwarder;
using Yarp.Telemetry.Consumption;

namespace PANiXiDA.Core.Observability.Server;

internal sealed class YarpForwarderMetrics : IForwarderTelemetryConsumer
{
    internal const string MeterName = "PANiXiDA.Core.Observability.Yarp";
    private readonly UpDownCounter<long> _activeRequests;
    private readonly Counter<long> _errors;

    public YarpForwarderMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _activeRequests = meter.CreateUpDownCounter<long>("yarp.proxy.active_requests", "{request}");
        _errors = meter.CreateCounter<long>("yarp.proxy.errors", "{error}");
    }

    public void OnForwarderStart(DateTime timestamp, string destinationPrefix)
    {
        _activeRequests.Add(1);
    }

    public void OnForwarderStop(DateTime timestamp, int statusCode)
    {
        _activeRequests.Add(-1);
    }

    public void OnForwarderFailed(DateTime timestamp, ForwarderError error)
    {
        _errors.Add(1, new KeyValuePair<string, object?>("error.type", error.ToString()));
    }
}
