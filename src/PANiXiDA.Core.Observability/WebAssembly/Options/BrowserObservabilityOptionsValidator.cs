using Microsoft.Extensions.Options;

namespace PANiXiDA.Core.Observability.WebAssembly.Options;

internal sealed class BrowserObservabilityOptionsValidator : IValidateOptions<BrowserObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, BrowserObservabilityOptions options)
    {
        return string.IsNullOrWhiteSpace(options.ServiceName)
            ? ValidateOptionsResult.Fail("OTEL_SERVICE_NAME must be configured when OTEL_EXPORTER_OTLP_ENDPOINT is set.")
            : ValidateOptionsResult.Success;
    }
}
