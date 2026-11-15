using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class StaticAssetsEndpointConventionBuilderExtensionsTests
{
    [Theory(DisplayName = "Static assets use one diagnostic route label when grouping is applied")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GroupStaticAssetMetrics_Should_GroupOnlyStaticEndpoints(int calls)
    {
        var manifestPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(manifestPath, """
                {
                  "Version": 1,
                  "ManifestType": "Publish",
                  "Endpoints": [
                    {
                      "Route": "site.css",
                      "AssetFile": "site.css",
                      "ResponseHeaders": [{ "Name": "ETag", "Value": "\"css\"" }]
                    },
                    {
                      "Route": "site.js",
                      "AssetFile": "site.js",
                      "ResponseHeaders": [{ "Name": "ETag", "Value": "\"js\"" }]
                    }
                  ]
                }
                """, TestContext.Current.CancellationToken);

            await using var app = WebApplication.CreateSlimBuilder().Build();
            var staticAssets = app.MapStaticAssets(manifestPath);
            app.MapGet("/health", () => "healthy");

            var configuredAssets = staticAssets;
            for (var call = 0; call < calls; call++)
            {
                configuredAssets = configuredAssets.GroupStaticAssetMetrics();
            }

            configuredAssets.ShortCircuit();

            configuredAssets.ShouldBeSameAs(staticAssets);
            var endpoints = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();
            foreach (var path in new[] { "site.css", "site.js" })
            {
                var endpoint = endpoints.Single(candidate => candidate.RoutePattern.RawText == path);
                var expectedLabel = calls == 0 ? path : "static-assets";
                endpoint.Metadata.GetOrderedMetadata<IRouteDiagnosticsMetadata>()
                    .ShouldHaveSingleItem().Route.ShouldBe(expectedLabel);
            }

            var health = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/health");
            health.Metadata.GetOrderedMetadata<IRouteDiagnosticsMetadata>()
                .ShouldHaveSingleItem().Route.ShouldBe("/health");
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    [Fact(DisplayName = "Static asset metric grouping requires an endpoint convention builder")]
    public void GroupStaticAssetMetrics_Should_RejectNullBuilder()
    {
        StaticAssetsEndpointConventionBuilder builder = null!;

        var exception = Should.Throw<ArgumentNullException>(() => builder.GroupStaticAssetMetrics());

        exception.ParamName.ShouldBe("builder");
    }
}
