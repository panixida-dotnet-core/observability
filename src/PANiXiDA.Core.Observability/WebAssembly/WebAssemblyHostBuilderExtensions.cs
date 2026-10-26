using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using PANiXiDA.Core.Observability.Options.BrowserObservability;

namespace PANiXiDA.Core.Observability.WebAssembly;

/// <summary>Configures telemetry for single-threaded Blazor WebAssembly applications.</summary>
public static class WebAssemblyHostBuilderExtensions
{
    /// <summary>Adds browser telemetry when an OTLP endpoint is configured.</summary>
    /// <param name="builder">The WebAssembly application builder.</param>
    /// <returns>The application builder.</returns>
    public static WebAssemblyHostBuilder AddObservability(this WebAssemblyHostBuilder builder)
    {
        builder.Services.AddBrowserObservability(new Uri(builder.HostEnvironment.BaseAddress), builder.Configuration);
        return builder;
    }

    /// <summary>Starts the telemetry providers before rendering the application.</summary>
    /// <param name="host">The built WebAssembly host.</param>
    /// <returns>The application host.</returns>
    public static WebAssemblyHost UseObservability(this WebAssemblyHost host)
    {
        host.Services.GetService<ITelemetryHostInitializer>()?.Initialize();
        return host;
    }

    internal static void AddBrowserObservability(this IServiceCollection services, Uri baseAddress, IConfiguration configuration)
    {
        ComponentsMetricsServiceCollectionExtensions.AddComponentsMetrics(services);
        ComponentsMetricsServiceCollectionExtensions.AddComponentsTracing(services);

        var settings = configuration.Get<BrowserObservabilityOptions>() ?? new BrowserObservabilityOptions();
        if (!settings.ExportEnabled)
        {
            return;
        }

        services.AddSingleton<IValidateOptions<BrowserObservabilityOptions>, BrowserObservabilityOptionsValidator>();
        services.AddOptions<BrowserObservabilityOptions>()
            .Bind(configuration)
            .ValidateOnStart();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddDetector(provider =>
                new BrowserServiceResourceDetector(provider.GetRequiredService<IOptions<BrowserObservabilityOptions>>())))
            .WithTracing(tracing => tracing.AddSource("Microsoft.AspNetCore.Components")
                .AddHttpClientInstrumentation()
                .AddOtlpExporter("traces", configure: null))
            .WithMetrics(metrics => metrics
                .AddMeter("Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Lifecycle")
                .AddHttpClientInstrumentation()
                .AddOtlpExporter("metrics", configure: null))
            .WithLogging(logging => logging.AddOtlpExporter("logs", configureExporter: null), options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });

        foreach (var signal in new[] { "logs", "metrics", "traces" })
        {
            services.AddOptions<OtlpExporterOptions>(signal)
                .Configure<IOptions<BrowserObservabilityOptions>>((exporter, browser) =>
                    ConfigureExporter(exporter, browser.Value, baseAddress, signal));
        }
    }

    private static void ConfigureExporter(OtlpExporterOptions options, BrowserObservabilityOptions settings, Uri baseAddress, string signal)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = settings.GetExporterEndpoint(baseAddress, signal);
        if (settings.MaxRequestSizeBytes is { } limit)
        {
            options.MaxRequestSizeBytes = limit;
        }
    }

    private sealed class BrowserServiceResourceDetector(IOptions<BrowserObservabilityOptions> options) : IResourceDetector
    {
        public Resource Detect()
        {
            var serviceName = options.Value.ServiceName;
            return ResourceBuilder.CreateEmpty()
                .AddService(serviceName, serviceInstanceId: serviceName)
                .Build();
        }
    }
}
