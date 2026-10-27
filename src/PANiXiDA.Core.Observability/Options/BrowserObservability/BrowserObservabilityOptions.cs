using Microsoft.Extensions.Configuration;

namespace PANiXiDA.Core.Observability.Options.BrowserObservability;

internal sealed class BrowserObservabilityOptions
{
    public const string ServiceNameKey = "OTEL_SERVICE_NAME";
    public const string EndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string MaxRequestSizeBytesKey = "OpenTelemetry:Otlp:MaxRequestSizeBytes";
    public const string PerformanceEnabledKey = "BrowserTelemetry:Performance:Enabled";
    public const int MinimumRequestSizeBytes = 750000;
    public const int MaximumRequestSizeBytes = 268435456;

    [ConfigurationKeyName(ServiceNameKey)]
    public string ServiceName { get; set; } = string.Empty;

    [ConfigurationKeyName(EndpointKey)]
    public string? Endpoint { get; set; }

    [ConfigurationKeyName(MaxRequestSizeBytesKey)]
    public int? MaxRequestSizeBytes { get; set; }

    [ConfigurationKeyName(PerformanceEnabledKey)]
    public bool PerformanceEnabled { get; set; } = true;

    public bool ExportEnabled => !string.IsNullOrWhiteSpace(Endpoint);

    public Uri GetExporterEndpoint(Uri baseAddress, string signal)
    {
        var endpoint = new Uri(baseAddress, $"{Endpoint!.TrimEnd('/')}/");
        return new Uri(endpoint, "v1/" + signal);
    }
}
