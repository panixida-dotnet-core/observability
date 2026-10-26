using System.Security.Claims;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using OpenTelemetry;

using PANiXiDA.Core.Observability.Options.BrowserTelemetry;

namespace PANiXiDA.Core.Observability.Server;

internal sealed class BrowserTelemetryForwarder : IDisposable
{
    private readonly BrowserTelemetryOptions _options;
    private readonly HttpClient _client;
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    public BrowserTelemetryForwarder(IOptions<BrowserTelemetryOptions> options, IHostEnvironment environment)
    {
        _options = options.Value;
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
        {
            BaseAddress = new Uri($"{_options.CollectorEndpoint.TrimEnd('/')}/"),
            Timeout = _options.RequestTimeout
        };
        _client.DefaultRequestHeaders.Add("X-Frontend-Service", _options.ServiceName);
        _client.DefaultRequestHeaders.Add("X-Frontend-Environment", environment.EnvironmentName.ToLowerInvariant());
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("sub") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = _options.RequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    }

    internal async Task<IResult> ForwardAsync(string signal, HttpContext context)
    {
        var authorizationStatus = await AuthorizeAsync(context);
        using var lease = _limiter.AttemptAcquire(context);
        if (!lease.IsAcquired)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (authorizationStatus is { } status)
        {
            return Results.StatusCode(status);
        }

        if (ValidateRequest(signal, context) is { } rejection)
        {
            return Results.StatusCode(rejection);
        }

        await using var body = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            if (body.Length + read > _options.MaxRequestBodySize)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            await body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
        }

        using var forwarding = new HttpRequestMessage(HttpMethod.Post, $"v1/{signal}")
        {
            Content = new ByteArrayContent(body.ToArray())
        };
        forwarding.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        using var suppression = SuppressInstrumentationScope.Begin();
        try
        {
            using var response = await _client.SendAsync(forwarding, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            return response.IsSuccessStatusCode
                ? Results.Bytes([], "application/x-protobuf")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _limiter.Dispose();
    }

    private async Task<int?> AuthorizeAsync(HttpContext context)
    {
        if (_options.AuthorizationPolicy is not { } policyName)
        {
            return context.User.Identity?.IsAuthenticated == true ? null : StatusCodes.Status401Unauthorized;
        }

        var provider = context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await provider.GetPolicyAsync(policyName)
            ?? throw new InvalidOperationException($"No policy found: {policyName}.");
        var evaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();
        var authentication = await evaluator.AuthenticateAsync(policy, context);
        if (!authentication.Succeeded)
        {
            return StatusCodes.Status401Unauthorized;
        }

        var result = await evaluator.AuthorizeAsync(policy, authentication, context, context);
        return result.Succeeded ? null : StatusCodes.Status403Forbidden;
    }

    private int? ValidateRequest(string signal, HttpContext context)
    {
        if (signal is not ("logs" or "metrics" or "traces"))
        {
            return StatusCodes.Status404NotFound;
        }

        var request = context.Request;
        if ((request.Headers["Sec-Fetch-Site"].Count > 0 && request.Headers["Sec-Fetch-Site"] != "same-origin")
            || (request.Headers.Origin.Count > 0 && !string.Equals(request.Headers.Origin.ToString(),
                $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)))
        {
            return StatusCodes.Status403Forbidden;
        }

        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || contentType.MediaType != "application/x-protobuf" || request.Headers.ContentEncoding.Count != 0)
        {
            return StatusCodes.Status415UnsupportedMediaType;
        }

        if (request.ContentLength is { } length && length > _options.MaxRequestBodySize)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        return null;
    }
}
