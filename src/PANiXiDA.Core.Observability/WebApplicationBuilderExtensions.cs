using System.Diagnostics.Metrics;
using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PANiXiDA.Core.Observability;

/// <summary>
/// Provides extension methods for configuring PANiXiDA observability.
/// </summary>
public static class WebApplicationBuilderExtensions
{
    /// <summary>
    /// Adds OpenTelemetry logging, metrics, and tracing configured for a PANiXiDA host.
    /// </summary>
    /// <param name="builder">The web application builder to configure.</param>
    /// <returns>The configured <see cref="WebApplicationBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> is <see langword="null"/>.
    /// </exception>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var serviceVersion = GetServiceVersion(Assembly.GetEntryAssembly());

        builder.Services.AddHttpContextAccessor();

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resourceBuilder =>
            {
                resourceBuilder
                    .AddService(
                        serviceName: builder.Environment.ApplicationName,
                        serviceVersion: serviceVersion,
                        serviceInstanceId: Environment.MachineName)
                    .AddEnvironmentVariableDetector()
                    .AddTelemetrySdk();
            })
            .WithTracing(tracingBuilder =>
            {
                tracingBuilder
                    .AddSource("Wolverine", "Quartz")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
            })
            .WithMetrics(metricsBuilder =>
            {
                metricsBuilder
                    .AddMeter("Wolverine*", "Quartz")
                    .AddView(ConfigureDurationHistogram)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddNpgsqlInstrumentation();
            })
            .WithLogging(
                configureBuilder: loggingBuilder => loggingBuilder.AddProcessor<ClientCancellationLogProcessor>(),
                configureOptions: loggingOptions =>
                {
                    loggingOptions.IncludeFormattedMessage = true;
                    loggingOptions.IncludeScopes = true;
                    loggingOptions.ParseStateValues = true;
                })
            .UseOtlpExporter();

        return builder;
    }

    private static MetricStreamConfiguration? ConfigureDurationHistogram(Instrument instrument)
    {
        double scale = instrument.Unit switch
        {
            "s" => 1,
            "ms" => 1000,
            _ => 0
        };
        if (scale == 0)
        {
            return null;
        }

        var needsBoundaries = instrument switch
        {
            Histogram<byte> histogram => histogram.Advice?.HistogramBucketBoundaries is null,
            Histogram<short> histogram => histogram.Advice?.HistogramBucketBoundaries is null,
            Histogram<int> histogram => histogram.Advice?.HistogramBucketBoundaries is null,
            Histogram<long> histogram => histogram.Advice?.HistogramBucketBoundaries is null,
            Histogram<float> histogram => histogram.Advice?.HistogramBucketBoundaries is null,
            Histogram<double> histogram => histogram.Advice?.HistogramBucketBoundaries is null,
            _ => false
        };
        if (!needsBoundaries)
        {
            return null;
        }

        double[] seconds = [0, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600, 1800, 3600];
        return new ExplicitBucketHistogramConfiguration
        {
            Boundaries = seconds.Select(boundary => boundary * scale).ToArray()
        };
    }

    private static string GetServiceVersion(Assembly? entryAssembly)
    {
        return entryAssembly?.GetName().Version?.ToString()
            ?? "unknown";
    }
}
