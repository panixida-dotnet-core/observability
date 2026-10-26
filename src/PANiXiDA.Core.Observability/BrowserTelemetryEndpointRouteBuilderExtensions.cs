using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Observability.Options.BrowserTelemetry;
using PANiXiDA.Core.Observability.Server;

namespace PANiXiDA.Core.Observability;

/// <summary>Maps the optional authenticated browser telemetry relay.</summary>
public static class BrowserTelemetryEndpointRouteBuilderExtensions
{
    /// <summary>Maps the configured relay when BrowserTelemetry:Enabled is true.</summary>
    /// <param name="endpoints">The application's endpoint builder.</param>
    /// <returns>The endpoint builder.</returns>
    public static IEndpointRouteBuilder MapBrowserTelemetry(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<BrowserTelemetryOptions>>().Value;
        if (options.Enabled)
        {
            endpoints.MapPost($"{options.Path.TrimEnd('/')}/v1/{{signal}}", (RequestDelegate)ForwardAsync)
                .WithMetadata(new SkipStatusCodePagesAttribute(), BrowserTelemetryEndpointMetadata.Instance)
                .AllowAnonymous();
        }

        return endpoints;
    }

    /// <summary>Checks endpoint metadata so host session policies can recognize telemetry independently of its URL.</summary>
    /// <param name="request">The current HTTP request.</param>
    /// <returns>Whether the selected endpoint is a browser telemetry relay.</returns>
    public static bool IsBrowserTelemetryRequest(this HttpRequest request) =>
        request.HttpContext.GetEndpoint()?.Metadata.GetMetadata<BrowserTelemetryEndpointMetadata>() is not null;

    internal static void AddBrowserTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(BrowserTelemetryOptions.SectionName);
        services.AddSingleton<IValidateOptions<BrowserTelemetryOptions>, BrowserTelemetryOptionsValidator>();
        services.AddOptions<BrowserTelemetryOptions>().Bind(section).ValidateOnStart();
        if (!section.GetValue<bool>(nameof(BrowserTelemetryOptions.Enabled)))
        {
            return;
        }

        services.AddSingleton<BrowserTelemetryForwarder>();
    }

    private static async Task ForwardAsync(HttpContext context)
    {
        var forwarder = context.RequestServices.GetRequiredService<BrowserTelemetryForwarder>();
        var result = await forwarder.ForwardAsync((string)context.Request.RouteValues["signal"]!, context);
        await result.ExecuteAsync(context);
    }
}
