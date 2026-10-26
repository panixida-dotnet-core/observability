namespace PANiXiDA.Core.Observability.Options.BrowserTelemetry;

/// <summary>Configures the optional authenticated browser-to-collector relay.</summary>
public sealed class BrowserTelemetryOptions
{
    /// <summary>The application configuration section.</summary>
    public const string SectionName = "BrowserTelemetry";

    /// <summary>Enables the relay. Disabled by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The application path prefix used by browser exporters.</summary>
    public string Path { get; set; } = "/telemetry";

    /// <summary>The internal HTTP or HTTPS collector endpoint.</summary>
    public string CollectorEndpoint { get; set; } = string.Empty;

    /// <summary>The trusted frontend service name forwarded to the collector.</summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>An optional host authorization policy, in addition to authentication.</summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>The maximum accepted protobuf body size in bytes.</summary>
    public int MaxRequestBodySize { get; set; } = 1024 * 1024;

    /// <summary>The maximum number of requests per minute for each user or IP address.</summary>
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>The maximum time allowed for forwarding a request.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
