using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry;
using OpenTelemetry.Metrics;

using Yarp.ReverseProxy.Forwarder;
using Yarp.Telemetry.Consumption;

namespace PANiXiDA.Core.Observability.UnitTests;

[Collection(typeof(YarpForwarderMetricsCollection))]
public sealed class YarpForwarderMetricsTests
{
    [Theory(DisplayName = "YARP should export active requests and forwarding errors by reason without treating HTTP statuses as forwarding failures")]
    [InlineData(ForwarderError.Request)]
    [InlineData(ForwarderError.RequestCanceled)]
    [InlineData(ForwarderError.ResponseHeaders)]
    [InlineData(ForwarderError.ResponseBodyDestination)]
    public void YarpMetrics_Should_TrackActiveRequestsAndFailureReasons(ForwarderError error)
    {
        var measurements = new Dictionary<(string Name, string? Reason), long>();
        using var exporter = new RecordingExporter(measurements);
        using var reader = new BaseExportingMetricReader(exporter);
        var builder = WebApplication.CreateBuilder();
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(reader));
        using var app = builder.Build();
        _ = app.Services.GetRequiredService<MeterProvider>();
        var consumer = app.Services.GetServices<IForwarderTelemetryConsumer>().Single();

        reader.Collect().ShouldBeTrue();
        measurements.ShouldBeEmpty();
        consumer.OnForwarderStart(DateTime.UtcNow, "https://destination.example.test/");
        consumer.OnForwarderStart(DateTime.UtcNow, "https://destination.example.test/");
        reader.Collect().ShouldBeTrue();
        measurements[("yarp.proxy.active_requests", null)].ShouldBe(2);

        consumer.OnForwarderStop(DateTime.UtcNow, 500);
        reader.Collect().ShouldBeTrue();
        measurements[("yarp.proxy.active_requests", null)].ShouldBe(1);
        measurements.Keys.ShouldNotContain(key => key.Name == "yarp.proxy.errors");

        consumer.OnForwarderFailed(DateTime.UtcNow, error);
        reader.Collect().ShouldBeTrue();
        measurements[("yarp.proxy.active_requests", null)].ShouldBe(1);
        measurements[("yarp.proxy.errors", error.ToString())].ShouldBe(1);

        consumer.OnForwarderStop(DateTime.UtcNow, 502);
        reader.Collect().ShouldBeTrue();
        measurements[("yarp.proxy.active_requests", null)].ShouldBe(0);
        measurements[("yarp.proxy.errors", error.ToString())].ShouldBe(1);
    }

    private sealed class RecordingExporter(Dictionary<(string Name, string? Reason), long> measurements) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (!metric.Name.StartsWith("yarp.proxy.", StringComparison.Ordinal)) continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    string? reason = null;
                    foreach (var tag in point.Tags)
                    {
                        tag.Key.ShouldBe("error.type");
                        reason = tag.Value.ShouldBeOfType<string>();
                    }
                    measurements[(metric.Name, reason)] = point.GetSumLong();
                }
            }
            return ExportResult.Success;
        }
    }
}

[CollectionDefinition(DisableParallelization = true)]
public sealed class YarpForwarderMetricsCollection;
