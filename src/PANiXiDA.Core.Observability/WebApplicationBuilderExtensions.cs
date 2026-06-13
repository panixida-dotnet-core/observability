using System.Reflection;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using OpenTelemetry;
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
    /// <param name="applicationAssembly">The application assembly used to resolve service version metadata.</param>
    /// <returns>The configured <see cref="WebApplicationBuilder"/> instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="builder"/> or <paramref name="applicationAssembly"/> is <see langword="null"/>.
    /// </exception>
    public static WebApplicationBuilder AddObservability(
        this WebApplicationBuilder builder,
        Assembly applicationAssembly)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(applicationAssembly);

        var serviceVersion = applicationAssembly.GetName().Version?.ToString()
            ?? "unknown";

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
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
            })
            .WithMetrics(metricsBuilder =>
            {
                metricsBuilder
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddNpgsqlInstrumentation();
            })
            .WithLogging(
                configureBuilder: null,
                configureOptions: loggingOptions =>
                {
                    loggingOptions.IncludeFormattedMessage = true;
                    loggingOptions.IncludeScopes = true;
                    loggingOptions.ParseStateValues = true;
                })
            .UseOtlpExporter();

        return builder;
    }
}
