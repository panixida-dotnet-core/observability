using Microsoft.AspNetCore.Http.Metadata;

namespace PANiXiDA.Core.Observability;

/// <summary>Identifies a browser telemetry relay endpoint for host session policies.</summary>
public sealed class BrowserTelemetryEndpointMetadata : ISkipStatusCodePagesMetadata
{
    private BrowserTelemetryEndpointMetadata() { }

    /// <summary>The shared marker for browser telemetry endpoints.</summary>
    public static BrowserTelemetryEndpointMetadata Instance { get; } = new();
}
