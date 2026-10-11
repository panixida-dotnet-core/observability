using System.Diagnostics.Metrics;
using System.Numerics;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class DurationHistogramTests
{
    private const string TestMeterName = "Tests.Duration";

    [Theory(DisplayName = "Duration histograms use equivalent time boundaries regardless of their meter")]
    [InlineData("Quartz", "quartz.job.execution.duration", "s", 1)]
    [InlineData("Quartz", "quartz.job.execution.duration", "ms", 1000)]
    [InlineData("Wolverine:Orders", "test.duration", "s", 1)]
    [InlineData("Wolverine:Orders", "test.duration", "ms", 1000)]
    [InlineData(TestMeterName, "test.duration", "s", 1)]
    [InlineData(TestMeterName, "test.duration", "ms", 1000)]
    public void DurationsShouldUseEquivalentBoundaries(string meterName, string metricName, string unit, double scale)
    {
        using var harness = new MetricsHarness(meterName);
        var histogram = harness.Meter.CreateHistogram<double>(metricName, unit);

        histogram.Record(0.0005 * scale, new KeyValuePair<string, object?>("result", "success"));
        histogram.Record(0.1495 * scale, new KeyValuePair<string, object?>("result", "success"));
        harness.Collect();

        var sample = harness.Histograms.Should().ContainSingle().Subject;
        sample.Unit.Should().Be(unit);
        sample.Count.Should().Be(2);
        sample.Sum.Should().BeApproximately(0.15 * scale, 0.000001);
        sample.Tags.Should().Contain("result", "success");
        sample.Buckets.Should().Contain((0.001 * scale, 1L)).And.Contain((0.25 * scale, 1L));
        sample.Buckets.Should().Contain(item => item.Bound == 3600 * scale);
    }

    [Theory(DisplayName = "All supported histogram types preserve explicit advice and otherwise use duration defaults")]
    [InlineData("s", AdviceKind.None)]
    [InlineData("ms", AdviceKind.None)]
    [InlineData("s", AdviceKind.Unspecified)]
    [InlineData("ms", AdviceKind.Unspecified)]
    [InlineData("s", AdviceKind.Explicit)]
    [InlineData("ms", AdviceKind.Explicit)]
    [InlineData("s", AdviceKind.NoBuckets)]
    [InlineData("ms", AdviceKind.NoBuckets)]
    public void HistogramsShouldRespectAdvice(string unit, AdviceKind adviceKind)
    {
        using var harness = new MetricsHarness(TestMeterName);
        RecordSamples<byte>(harness.Meter, "test.byte", unit, adviceKind);
        RecordSamples<short>(harness.Meter, "test.short", unit, adviceKind);
        RecordSamples<int>(harness.Meter, "test.int", unit, adviceKind);
        RecordSamples<long>(harness.Meter, "test.long", unit, adviceKind);
        RecordSamples<float>(harness.Meter, "test.float", unit, adviceKind);
        RecordSamples<double>(harness.Meter, "test.double", unit, adviceKind);

        harness.Collect();

        harness.Histograms.Should().HaveCount(6);
        foreach (var sample in harness.Histograms)
        {
            sample.Unit.Should().Be(unit);
            sample.Count.Should().Be(2);
            sample.Sum.Should().Be(5);
            if (adviceKind == AdviceKind.Explicit)
            {
                sample.Buckets.Should().Equal((1, 0L), (4, 2L), (double.PositiveInfinity, 0L));
            }
            else if (adviceKind == AdviceKind.NoBuckets)
            {
                sample.Buckets.Should().BeEmpty();
            }
            else
            {
                double scale = unit == "s" ? 1 : 1000;
                sample.Buckets.Should().Contain(item => item.Bound == 0.001 * scale);
                sample.Buckets.Should().Contain(item => item.Bound == 3600 * scale);
            }
        }
    }

    [Theory(DisplayName = "Histograms without a supported time unit retain SDK defaults")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("By")]
    [InlineData("{message}")]
    [InlineData("us")]
    public void OtherUnitsShouldKeepDefaultBoundaries(string? unit)
    {
        using var harness = new MetricsHarness(TestMeterName);
        harness.Meter.CreateHistogram<double>("test.size", unit).Record(2);

        harness.Collect();

        var sample = harness.Histograms.Should().ContainSingle().Subject;
        sample.Buckets.Should().Contain((5, 1L));
        sample.Buckets.Should().NotContain(item => item.Bound == 0.001 || item.Bound == 1);
    }

    [Theory(DisplayName = "Time units do not turn counters into histograms")]
    [InlineData("s")]
    [InlineData("ms")]
    public void NonHistogramInstrumentsShouldRemainUnchanged(string unit)
    {
        using var harness = new MetricsHarness(TestMeterName);
        harness.Meter.CreateCounter<long>("test.counter", unit).Add(3);

        harness.Collect();

        harness.Histograms.Should().BeEmpty();
        harness.CounterValues.Should().ContainSingle().Which.Should().Be(3);
    }

    private static void RecordSamples<T>(Meter meter, string name, string unit, AdviceKind adviceKind)
        where T : struct, INumber<T>
    {
        InstrumentAdvice<T>? advice = adviceKind switch
        {
            AdviceKind.None => null,
            AdviceKind.Unspecified => new InstrumentAdvice<T>(),
            AdviceKind.Explicit => new InstrumentAdvice<T> { HistogramBucketBoundaries = [T.One, T.CreateChecked(4)] },
            _ => new InstrumentAdvice<T> { HistogramBucketBoundaries = [] }
        };
        var histogram = meter.CreateHistogram<T>(name, unit, advice: advice);
        histogram.Record(T.CreateChecked(2));
        histogram.Record(T.CreateChecked(3));
    }

    public enum AdviceKind
    {
        None,
        Unspecified,
        Explicit,
        NoBuckets
    }

    private sealed class MetricsHarness : IDisposable
    {
        private readonly WebApplication app;
        private readonly BaseExportingMetricReader reader;
        public Meter Meter { get; }
        public List<HistogramSample> Histograms { get; } = [];
        public List<long> CounterValues { get; } = [];

        public MetricsHarness(string meterName)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
                ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1"
            });
            builder.AddObservability();
            reader = new BaseExportingMetricReader(new RecordingExporter(metric =>
            {
                if (metric.MeterName != meterName)
                {
                    return;
                }

                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    if (metric.MetricType == MetricType.LongSum)
                    {
                        CounterValues.Add(point.GetSumLong());
                        continue;
                    }

                    var buckets = new List<(double Bound, long Count)>();
                    if (point.GetHistogramBuckets() is { } histogramBuckets)
                    {
                        foreach (var bucket in histogramBuckets)
                        {
                            buckets.Add((bucket.ExplicitBound, bucket.BucketCount));
                        }
                    }

                    var tags = new Dictionary<string, object?>();
                    foreach (var tag in point.Tags)
                    {
                        tags.Add(tag.Key, tag.Value);
                    }

                    Histograms.Add(new HistogramSample(metric.Unit, point.GetHistogramCount(),
                        point.GetHistogramSum(), tags, buckets));
                }
            }));
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(meterName).AddReader(reader));
            app = builder.Build();
            _ = app.Services.GetRequiredService<MeterProvider>();
            Meter = new Meter(meterName);
        }

        public void Collect()
        {
            reader.Collect().Should().BeTrue();
        }

        public void Dispose()
        {
            Meter.Dispose();
            ((IDisposable)app).Dispose();
        }
    }

    private sealed record HistogramSample(string? Unit, long Count, double Sum,
        Dictionary<string, object?> Tags, List<(double Bound, long Count)> Buckets);

    private sealed class RecordingExporter(Action<Metric> record) : BaseExporter<Metric>
    {
        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                record(metric);
            }

            return ExportResult.Success;
        }
    }
}
