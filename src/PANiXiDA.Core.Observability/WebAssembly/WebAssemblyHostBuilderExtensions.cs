using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PANiXiDA.Core.Observability.WebAssembly;

/// <summary>Configures telemetry for single-threaded Blazor WebAssembly applications.</summary>
public static class WebAssemblyHostBuilderExtensions
{
    /// <summary>Adds browser telemetry when an OTLP endpoint is configured.</summary>
    /// <param name="builder">The WebAssembly application builder.</param>
    /// <returns>The application builder.</returns>
    public static WebAssemblyHostBuilder AddObservability(this WebAssemblyHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddBrowserObservability(new Uri(builder.HostEnvironment.BaseAddress), builder.Configuration);
        return builder;
    }

    /// <summary>Starts the telemetry providers before rendering the application.</summary>
    /// <param name="host">The built WebAssembly host.</param>
    /// <returns>The application host.</returns>
    public static WebAssemblyHost UseObservability(this WebAssemblyHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        host.Services.GetService<ITelemetryHostInitializer>()?.Initialize();
        return host;
    }

    internal static void AddBrowserObservability(this IServiceCollection services, Uri baseAddress, IConfiguration configuration)
    {
        services.AddHttpClient();
        ComponentsMetricsServiceCollectionExtensions.AddComponentsMetrics(services);
        ComponentsMetricsServiceCollectionExtensions.AddComponentsTracing(services);

        var pathBase = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(pathBase))
        {
            return;
        }

        var serviceName = configuration["OTEL_SERVICE_NAME"]
            ?? throw new InvalidOperationException("OTEL_SERVICE_NAME must be configured when OTEL_EXPORTER_OTLP_ENDPOINT is set.");
        var endpoint = new Uri(baseAddress, pathBase.TrimEnd('/') + "/");
        var maxRequestSizeBytes = configuration.GetValue<int?>("OpenTelemetry:Otlp:MaxRequestSizeBytes");
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceInstanceId: serviceName))
            .WithTracing(tracing => tracing.AddSource("Microsoft.AspNetCore.Components")
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options => ConfigureExporter(options, endpoint, "traces", maxRequestSizeBytes)))
            .WithMetrics(metrics => metrics
                .AddMeter("Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Lifecycle")
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(options => ConfigureExporter(options, endpoint, "metrics", maxRequestSizeBytes)))
            .WithLogging(logging => logging.AddOtlpExporter(options => ConfigureExporter(options, endpoint, "logs", maxRequestSizeBytes)), options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });
    }

    private static void ConfigureExporter(OtlpExporterOptions options, Uri endpoint, string signal, int? maxRequestSizeBytes)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = new Uri(endpoint, "v1/" + signal);
        if (maxRequestSizeBytes is { } limit)
        {
            options.MaxRequestSizeBytes = limit;
        }
    }
}
