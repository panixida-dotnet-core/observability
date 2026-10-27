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
    private const string DeploymentEnvironmentNameAttribute = "deployment.environment.name";

    /// <summary>
    /// Adds OpenTelemetry logging, metrics, and tracing configured for a PANiXiDA host.
    /// </summary>
    /// <param name="builder">The web application builder to configure.</param>
    /// <returns>The configured <see cref="WebApplicationBuilder"/> instance.</returns>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        builder.Services.AddBrowserTelemetry(builder.Configuration);
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
                    .AddAttributes([new KeyValuePair<string, object>(
                        DeploymentEnvironmentNameAttribute, builder.Environment.EnvironmentName.ToLowerInvariant())])
                    .AddEnvironmentVariableDetector()
                    .AddTelemetrySdk();
            })
            .WithTracing(tracingBuilder =>
            {
                tracingBuilder
                    .AddSource("Wolverine", "Quartz", "Yarp.ReverseProxy")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();
            })
            .WithMetrics(metricsBuilder =>
            {
                metricsBuilder
                    .AddMeter("Wolverine*", "Quartz")
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

    private static string GetServiceVersion(Assembly? entryAssembly)
    {
        return entryAssembly?.GetName().Version?.ToString()
            ?? "unknown";
    }
}
