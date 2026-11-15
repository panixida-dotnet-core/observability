using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.StaticAssets;

namespace PANiXiDA.Core.Observability;

/// <summary>Provides telemetry conventions for mapped static assets.</summary>
public static class StaticAssetsEndpointConventionBuilderExtensions
{
    private static readonly IRouteDiagnosticsMetadata GroupedRoute = new GroupedStaticAssetRouteMetadata();

    /// <summary>Groups the metrics of the mapped static assets under the <c>static-assets</c> route label.</summary>
    /// <param name="builder">The endpoint convention builder returned by static asset mapping.</param>
    /// <returns>The original builder for chaining.</returns>
    public static StaticAssetsEndpointConventionBuilder GroupStaticAssetMetrics(
        this StaticAssetsEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Finally(static endpoint =>
        {
            if (!endpoint.Metadata.Contains(GroupedRoute))
            {
                endpoint.Metadata.Add(GroupedRoute);
            }
        });

        return builder;
    }

    private sealed class GroupedStaticAssetRouteMetadata : IRouteDiagnosticsMetadata
    {
        public string Route => "static-assets";
    }
}
