using Microsoft.Extensions.Options;

namespace PANiXiDA.Core.Observability.Options.BrowserTelemetry;

internal sealed class BrowserTelemetryOptionsValidator : IValidateOptions<BrowserTelemetryOptions>
{
    public ValidateOptionsResult Validate(string? name, BrowserTelemetryOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (!IsCollectorEndpointValid(options.CollectorEndpoint))
        {
            failures.Add("BrowserTelemetry:CollectorEndpoint must be an absolute HTTP/HTTPS base URL without a query or fragment.");
        }

        if (string.IsNullOrWhiteSpace(options.ServiceName) || options.ServiceName.Any(char.IsControl))
        {
            failures.Add("BrowserTelemetry:ServiceName must be a nonblank header value without control characters.");
        }

        if (string.IsNullOrWhiteSpace(options.Path) || !options.Path.StartsWith('/')
            || options.Path.StartsWith("//", StringComparison.Ordinal)
            || options.Path.IndexOfAny(['?', '#', '{', '}']) >= 0)
        {
            failures.Add("BrowserTelemetry:Path must be an absolute application path without a query, fragment, or route parameters.");
        }

        if (options.AuthorizationPolicy is not null && string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
        {
            failures.Add("BrowserTelemetry:AuthorizationPolicy must be nonblank when configured.");
        }

        if (options.MaxRequestBodySize <= 0)
        {
            failures.Add("BrowserTelemetry:MaxRequestBodySize must be positive.");
        }

        if (options.RequestsPerMinute <= 0)
        {
            failures.Add("BrowserTelemetry:RequestsPerMinute must be positive.");
        }

        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout.TotalMilliseconds > int.MaxValue)
        {
            failures.Add("BrowserTelemetry:RequestTimeout must be positive and at most 2147483647 milliseconds.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsCollectorEndpointValid(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var collector)
        && (collector.Scheme == Uri.UriSchemeHttp || collector.Scheme == Uri.UriSchemeHttps)
        && collector.Query.Length == 0 && collector.Fragment.Length == 0;
}
