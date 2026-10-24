using System.Reflection;

using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace PANiXiDA.Core.Observability.WebAssembly;

/// <summary>Configures telemetry for single-threaded Blazor WebAssembly applications.</summary>
public static class WebAssemblyHostBuilderExtensions
{
    /// <summary>Adds browser telemetry using a same-origin OTLP HTTP endpoint.</summary>
    /// <param name="builder">The WebAssembly application builder.</param>
    /// <returns>The application builder.</returns>
    public static WebAssemblyHostBuilder AddObservability(this WebAssemblyHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var endpoint = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "telemetry/");
        var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? (Assembly.GetEntryAssembly()?.GetName().Name ?? "blazor") + "-client";
        builder.Services.AddBrowserObservability(endpoint, serviceName);
        return builder;
    }

    /// <summary>Starts the telemetry providers before rendering the application.</summary>
    /// <param name="host">The built WebAssembly host.</param>
    /// <returns>The application host.</returns>
    public static WebAssemblyHost UseObservability(this WebAssemblyHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ = host.Services.GetRequiredService<TracerProvider>();
        _ = host.Services.GetRequiredService<MeterProvider>();
        _ = host.Services.GetRequiredService<BrowserOtlpTransport>();
        return host;
    }

    internal static void AddBrowserObservability(this IServiceCollection services, Uri endpoint, string serviceName)
    {
        var transport = new Lazy<BrowserOtlpTransport>(() => new BrowserOtlpTransport(endpoint));
        var instanceId = Guid.NewGuid().ToString("N");
        services.AddSingleton(_ => transport.Value);
        services.AddHttpClient();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new BrowserHttpTelemetryFilter(endpoint));
        services.Configure<BatchExportActivityProcessorOptions>(processor =>
        {
            processor.MaxQueueSize = 256;
            processor.MaxExportBatchSize = 64;
            processor.ScheduledDelayMilliseconds = 5000;
        });
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceInstanceId: instanceId,
                serviceVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString()))
            .WithTracing(tracing => tracing.AddSource("*").AddHttpClientInstrumentation()
                .AddOtlpExporter(options => ConfigureExporter(options, transport, endpoint, "traces")))
            .WithMetrics(metrics => metrics.AddMeter("*")
                .AddOtlpExporter((options, reader) =>
                {
                    ConfigureExporter(options, transport, endpoint, "metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15000;
                }))
            .WithLogging(logging => logging.AddOtlpExporter((options, processor) =>
            {
                ConfigureExporter(options, transport, endpoint, "logs");
                processor.BatchExportProcessorOptions.MaxQueueSize = 256;
                processor.BatchExportProcessorOptions.MaxExportBatchSize = 64;
                processor.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 5000;
            }), options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.ParseStateValues = true;
            });
    }

    private static void ConfigureExporter(OtlpExporterOptions options, Lazy<BrowserOtlpTransport> transport, Uri endpoint, string signal)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = new Uri(endpoint, "v1/" + signal);
        options.MaxRequestSizeBytes = 1024 * 1024;
        options.HttpClientFactory = () => new HttpClient(transport.Value, disposeHandler: false);
    }
}
