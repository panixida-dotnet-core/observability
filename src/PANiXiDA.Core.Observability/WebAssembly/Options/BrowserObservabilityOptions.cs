using Microsoft.Extensions.Configuration;

namespace PANiXiDA.Core.Observability.WebAssembly.Options;

internal sealed class BrowserObservabilityOptions
{
    [ConfigurationKeyName("OTEL_SERVICE_NAME")]
    public string ServiceName { get; set; } = string.Empty;
}
