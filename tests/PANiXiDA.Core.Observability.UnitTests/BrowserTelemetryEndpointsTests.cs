using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PANiXiDA.Core.Observability.Server;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class BrowserTelemetryEndpointsTests
{
    [Theory(DisplayName = "Browser telemetry should enforce configured bytes when body limit is configured")]
    [InlineData(1048576, 1048576, false, HttpStatusCode.OK)]
    [InlineData(2097152, 1500000, false, HttpStatusCode.OK)]
    [InlineData(2097152, 2097152, true, HttpStatusCode.OK)]
    [InlineData(2097152, 2097153, false, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(2097152, 2097153, true, HttpStatusCode.RequestEntityTooLarge)]
    public async Task BrowserTelemetry_Should_EnforceConfiguredBytes_When_BodyLimitIsConfigured(
        int limit, int size, bool chunked, HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken, limit);
        using var request = CreateRequest("logs", authenticated: true);
        request.Content = chunked ? new ChunkedContent(size) : new ByteArrayContent(new byte[size]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(expected);
        if (expected == HttpStatusCode.OK)
        {
            fixture.Received.ShouldHaveSingleItem().Body.Length.ShouldBe(size);
        }
        else
        {
            fixture.Received.ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Browser telemetry should reject startup when body limit is not positive")]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task BrowserTelemetry_Should_RejectStartup_When_BodyLimitIsNotPositive(int limit)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BrowserTelemetry:Enabled"] = "true",
            ["BrowserTelemetry:CollectorEndpoint"] = "http://127.0.0.1:1/",
            ["BrowserTelemetry:ServiceName"] = "test-client",
            ["BrowserTelemetry:MaxRequestBodySize"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        builder.AddObservability();
        await using var app = builder.Build();

        await Should.ThrowAsync<OptionsValidationException>(() => app.StartAsync(TestContext.Current.CancellationToken));
    }

    [Theory(DisplayName = "Browser telemetry should forward only protobuf and server-owned identity when authenticated client exports")]
    [InlineData("logs")]
    [InlineData("metrics")]
    [InlineData("traces")]
    public async Task BrowserTelemetry_Should_ForwardProtobufAndServerIdentity_When_AuthenticatedClientExports(string signal)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var request = CreateRequest(signal, authenticated: true);
        request.Headers.Add("Cookie", "private-cookie=do-not-forward");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "do-not-forward");
        request.Headers.Add("X-Frontend-Service", "forged-server");

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var received = fixture.Received.ShouldHaveSingleItem();
        received.Path.ShouldBe("/v1/" + signal);
        received.Body.ShouldBe([1, 2, 3]);
        received.Service.ShouldBe("tactical-heroes-admin-client-development");
        received.Environment.ShouldBe("development");
        received.Cookie.ShouldBeEmpty();
        received.Authorization.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Browser telemetry should reject invalid exports when authentication origin format or size is invalid")]
    [InlineData(false, "logs", "application/x-protobuf", 3, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, "unknown", "application/x-protobuf", 3, false, HttpStatusCode.NotFound)]
    [InlineData(true, "logs", "application/json", 3, false, HttpStatusCode.UnsupportedMediaType)]
    [InlineData(true, "logs", "application/x-protobuf", 1048577, false, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(true, "logs", "application/x-protobuf", 3, true, HttpStatusCode.Forbidden)]
    public async Task BrowserTelemetry_Should_RejectInvalidExports_When_AuthenticationOriginFormatOrSizeIsInvalid(
        bool authenticated, string signal, string contentType, int size, bool crossOrigin, HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var request = CreateRequest(signal, authenticated);
        request.Content = new ByteArrayContent(new byte[size]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (crossOrigin)
        {
            request.Headers.Add("Origin", "https://external.example.test");
        }

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(expected);
        fixture.Received.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Browser telemetry should reject oversized bodies when content length is unknown")]
    public async Task BrowserTelemetry_Should_RejectOversizedBodies_When_ContentLengthIsUnknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var request = CreateRequest("metrics", authenticated: true);
        request.Content = new ChunkedContent(1048577);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        fixture.Received.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Browser telemetry should return 429 when a session exceeds its export budget")]
    public async Task BrowserTelemetry_Should_Return429_When_ASessionExceedsItsExportBudget()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        for (int index = 0; index < 120; index++)
        {
            using var request = CreateRequest("logs", authenticated: true);
            using var response = await fixture.Client.SendAsync(request, cancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var excessRequest = CreateRequest("logs", authenticated: true);
        using var excessResponse = await fixture.Client.SendAsync(excessRequest, cancellationToken);

        excessResponse.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        fixture.Received.Count.ShouldBe(120);
    }

    [Fact(DisplayName = "Browser relay should remain unmapped and unregistered by default")]
    public async Task MapBrowserTelemetry_Should_RemainDisabled_When_NotConfigured()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.AddObservability();
        await using var app = builder.Build();

        var result = app.MapBrowserTelemetry();

        result.ShouldBeSameAs(app);
        ((IEndpointRouteBuilder)app).DataSources.ShouldBeEmpty();
        app.Services.GetService<BrowserTelemetryForwarder>().ShouldBeNull();
    }

    [Fact(DisplayName = "Telemetry request detection should use endpoint metadata instead of URL prefixes")]
    public void IsBrowserTelemetryRequest_Should_UseEndpointMetadata_When_PathChanges()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/telemetry/v1/logs";
        context.Request.IsBrowserTelemetryRequest().ShouldBeFalse();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "ordinary endpoint"));
        context.Request.IsBrowserTelemetryRequest().ShouldBeFalse();
        context.Request.Path = "/custom-observability/v1/logs";
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(BrowserTelemetryEndpointMetadata.Instance), "browser telemetry"));

        context.Request.IsBrowserTelemetryRequest().ShouldBeTrue();
    }

    [Theory(DisplayName = "Browser relay should reject cross-site origins encodings and malformed content types")]
    [InlineData("Sec-Fetch-Site", "cross-site", null, HttpStatusCode.Forbidden)]
    [InlineData("Sec-Fetch-Site", "same-origin", null, HttpStatusCode.OK)]
    [InlineData("Content-Encoding", "gzip", null, HttpStatusCode.UnsupportedMediaType)]
    [InlineData(null, null, null, HttpStatusCode.OK)]
    [InlineData(null, null, "malformed;", HttpStatusCode.UnsupportedMediaType)]
    public async Task BrowserTelemetry_Should_EnforceBrowserHeaders_When_ClientExports(string? header, string? value, string? contentType, HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var request = CreateRequest("logs", authenticated: true);
        if (header == "Content-Encoding")
        {
            request.Content!.Headers.TryAddWithoutValidation(header, value);
        }
        else if (header is not null)
        {
            request.Headers.TryAddWithoutValidation(header, value);
        }
        request.Headers.TryAddWithoutValidation("Origin", fixture.Client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        if (contentType is not null)
        {
            request.Content!.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory(DisplayName = "Browser relay should honor host authorization policy without issuing a login challenge")]
    [InlineData(false, HttpStatusCode.Forbidden)]
    [InlineData(true, HttpStatusCode.OK)]
    public async Task BrowserTelemetry_Should_ApplyHostPolicy_When_Configured(bool allowed, HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken, settings: new Dictionary<string, string?>
        {
            ["BrowserTelemetry:AuthorizationPolicy"] = "TelemetryPolicy"
        });
        using var request = CreateRequest("logs", authenticated: true);
        if (allowed)
        {
            request.Headers.Add("X-Test-Allowed", "true");
        }

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(expected);
    }

    [Theory(DisplayName = "Browser relay should support claim and address rate limit partitions")]
    [InlineData("name-id", false)]
    [InlineData("no-id", false)]
    [InlineData("no-id", true)]
    public async Task BrowserTelemetry_Should_SupportIdentityFallbacks_When_SubjectIsMissing(string identity, bool noAddress)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var request = CreateRequest("logs", authenticated: true);
        request.Headers.Add("X-Test-Identity", identity);
        if (noAddress)
        {
            request.Headers.Add("X-Test-No-Address", "true");
        }

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "Browser relay should reject principals without any identity")]
    public async Task BrowserTelemetry_Should_Return401_When_IdentityIsAbsent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken);
        using var request = CreateRequest("logs", authenticated: false);
        request.Headers.Add("X-Test-Empty-Identity", "true");

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory(DisplayName = "Browser relay should return 502 without following collector redirects or errors")]
    [InlineData(302)]
    [InlineData(500)]
    public async Task BrowserTelemetry_Should_Return502_When_CollectorRejects(int status)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken, collectorStatus: status);
        using var request = CreateRequest("logs", authenticated: true);

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Theory(DisplayName = "Browser relay should return 503 when collector is unavailable or times out")]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task BrowserTelemetry_Should_Return503_When_CollectorIsUnavailable(bool stopped, bool slow)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken, settings: new Dictionary<string, string?>
        {
            ["BrowserTelemetry:RequestTimeout"] = "00:00:00.1"
        }, stopCollector: stopped, slowCollector: slow);
        using var request = CreateRequest("logs", authenticated: true);

        using var response = await fixture.Client.SendAsync(request, cancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(DisplayName = "Browser relay should honor custom path and request limit while ignoring collector cookies")]
    public async Task BrowserTelemetry_Should_HonorConfiguration_When_PathAndBudgetChange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.StartAsync(cancellationToken, settings: new Dictionary<string, string?>
        {
            ["BrowserTelemetry:Path"] = "/custom-observability/",
            ["BrowserTelemetry:RequestsPerMinute"] = "2"
        });
        for (int index = 0; index < 3; index++)
        {
            using var request = CreateRequest("traces", authenticated: true);
            request.RequestUri = new Uri("custom-observability/v1/traces", UriKind.Relative);

            using var response = await fixture.Client.SendAsync(request, cancellationToken);

            response.StatusCode.ShouldBe(index < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests);
        }
        fixture.Received.Count.ShouldBe(2);
        fixture.Received.ShouldAllBe(export => export.Cookie.Length == 0);
    }
    private static HttpRequestMessage CreateRequest(string signal, bool authenticated)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "telemetry/v1/" + signal)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        if (authenticated)
        {
            request.Headers.Add("X-Test-Authenticated", "true");
        }

        return request;
    }

    private sealed record ReceivedExport(string Path, byte[] Body, string Service, string Environment, string Cookie, string Authorization);

    private sealed class Fixture(WebApplication backend, WebApplication bff, HttpClient client, ConcurrentQueue<ReceivedExport> received) : IAsyncDisposable
    {
        internal HttpClient Client => client;
        internal ConcurrentQueue<ReceivedExport> Received => received;

        internal static async Task<Fixture> StartAsync(CancellationToken cancellationToken, int? bodyLimit = null, Dictionary<string, string?>? settings = null, int collectorStatus = 200, bool stopCollector = false, bool slowCollector = false)
        {
            var received = new ConcurrentQueue<ReceivedExport>();
            var backendBuilder = WebApplication.CreateSlimBuilder();
            backendBuilder.Logging.ClearProviders();
            var backend = backendBuilder.Build();
            backend.Urls.Add("http://127.0.0.1:0");
            backend.MapPost("/v1/{signal}", async (string signal, HttpContext context) =>
            {
                await using var body = new MemoryStream();
                await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                received.Enqueue(new ReceivedExport($"/v1/{signal}", body.ToArray(),
                    context.Request.Headers["X-Frontend-Service"].ToString(),
                    context.Request.Headers["X-Frontend-Environment"].ToString(),
                    context.Request.Headers.Cookie.ToString(), context.Request.Headers.Authorization.ToString()));
                context.Response.Headers.SetCookie = "collector-secret=do-not-replay";
                if (collectorStatus == 302)
                {
                    context.Response.Headers.Location = "/redirect-target";
                }
                if (slowCollector)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
                }
                return Results.StatusCode(collectorStatus);
            });
            backend.MapGet("/redirect-target", () => Results.Ok());
            await backend.StartAsync(cancellationToken);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BrowserTelemetry:Enabled"] = "true",
                ["BrowserTelemetry:CollectorEndpoint"] = backend.Urls.Single(),
                ["BrowserTelemetry:ServiceName"] = "tactical-heroes-admin-client-development",
                ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "100"
            });
            if (bodyLimit is { } limit)
            {
                builder.Configuration["BrowserTelemetry:MaxRequestBodySize"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (settings is not null)
            {
                builder.Configuration.AddInMemoryCollection(settings);
            }
            builder.Services.AddAuthorization(options => options.AddPolicy("TelemetryPolicy", policy => policy.RequireClaim("telemetry-access", "true")));
            builder.AddObservability();
            var bff = builder.Build();
            bff.Urls.Add("http://127.0.0.1:0");
            bff.UseStatusCodePagesWithReExecute("/status-code");
            bff.UseRouting();
            bff.Use(async (context, next) =>
            {
                if (context.Request.Headers.ContainsKey("X-Test-Authenticated"))
                {
                    var mode = context.Request.Headers["X-Test-Identity"].ToString();
                    var claims = new List<Claim>();
                    if (mode != "no-id")
                    {
                        claims.Add(new Claim(mode == "name-id" ? ClaimTypes.NameIdentifier : "sub", "synthetic-user"));
                    }
                    if (context.Request.Headers.ContainsKey("X-Test-Allowed"))
                    {
                        claims.Add(new Claim("telemetry-access", "true"));
                    }
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
                }

                if (context.Request.Headers.ContainsKey("X-Test-Empty-Identity"))
                {
                    context.User = new ClaimsPrincipal();
                }
                if (context.Request.Headers.ContainsKey("X-Test-No-Address"))
                {
                    context.Connection.RemoteIpAddress = null;
                }
                await next();
            });

            bff.MapBrowserTelemetry();
            bff.MapMethods("/status-code", [HttpMethods.Get, HttpMethods.Post], () => Results.BadRequest());
            await bff.StartAsync(cancellationToken);
            if (stopCollector)
            {
                await backend.StopAsync(cancellationToken);
            }
            return new Fixture(backend, bff, new HttpClient { BaseAddress = new Uri(bff.Urls.Single()) }, received);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await bff.DisposeAsync();
            await backend.DisposeAsync();
        }
    }

    private sealed class ChunkedContent(int size) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[size]).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
