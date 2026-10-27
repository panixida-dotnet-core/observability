using Microsoft.Extensions.Options;

namespace PANiXiDA.Core.Observability.Options.BrowserObservability;

internal sealed class BrowserObservabilityOptionsValidator : IValidateOptions<BrowserObservabilityOptions>
{
    public ValidateOptionsResult Validate(string? name, BrowserObservabilityOptions options)
    {
        if (!options.ExportEnabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ServiceName))
        {
            failures.Add($"{BrowserObservabilityOptions.ServiceNameKey} must be configured when {BrowserObservabilityOptions.EndpointKey} is set.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.RelativeOrAbsolute, out var endpoint)
            || (endpoint.IsAbsoluteUri && endpoint.Scheme is not ("http" or "https")))
        {
            failures.Add($"{BrowserObservabilityOptions.EndpointKey} must be a relative URI or an absolute HTTP/HTTPS URI.");
        }

        if (options.MaxRequestSizeBytes is < BrowserObservabilityOptions.MinimumRequestSizeBytes or > BrowserObservabilityOptions.MaximumRequestSizeBytes)
        {
            failures.Add($"{BrowserObservabilityOptions.MaxRequestSizeBytesKey} must be between {BrowserObservabilityOptions.MinimumRequestSizeBytes} and {BrowserObservabilityOptions.MaximumRequestSizeBytes} bytes.");
        }

        if (options.JavaScriptErrorsEnabled && options.JavaScriptErrorsPerMinute is < 1 or > 1000)
        {
            failures.Add($"{BrowserObservabilityOptions.JavaScriptErrorsPerMinuteKey} must be between 1 and 1000.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
