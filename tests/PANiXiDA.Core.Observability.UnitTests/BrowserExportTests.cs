using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using PANiXiDA.Core.Observability.WebAssembly;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class BrowserExportTests
{
    [Theory(DisplayName = "Browser observability should stay disabled when endpoint is missing")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BrowserObservability_Should_StayDisabled_When_EndpointIsMissing(string? endpoint)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint
        }).Build();

        services.AddBrowserObservability(new Uri("https://admin.example.test/"), configuration);
        services.ShouldNotContain(service => service.ServiceType.FullName == "System.Net.Http.IHttpClientFactory");
        using var provider = services.BuildServiceProvider();

        provider.GetService<TracerProvider>().ShouldBeNull();
        provider.GetService<MeterProvider>().ShouldBeNull();
        provider.GetService<ITelemetryHostInitializer>().ShouldBeNull();
    }

    [Theory(DisplayName = "Browser observability should reject configuration when service name is missing or blank")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BrowserObservability_Should_RejectConfiguration_When_ServiceNameIsMissingOrBlank(string? serviceName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "/telemetry",
            ["OTEL_SERVICE_NAME"] = serviceName
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBrowserObservability(new Uri("https://admin.example.test/"), configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<TracerProvider>());
        exception.Failures.ShouldContain("OTEL_SERVICE_NAME must be configured when OTEL_EXPORTER_OTLP_ENDPOINT is set.");
    }

    [Theory(DisplayName = "Browser observability should reject configuration when payload limit is unsupported")]
    [InlineData(749999)]
    [InlineData(268435457)]
    public void BrowserObservability_Should_RejectConfiguration_When_PayloadLimitIsUnsupported(int limit)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "browser-export-test",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "/telemetry",
            ["OpenTelemetry:Otlp:MaxRequestSizeBytes"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBrowserObservability(new Uri("https://admin.example.test/"), configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<TracerProvider>());
        exception.Failures.ShouldContain("OpenTelemetry:Otlp:MaxRequestSizeBytes must be between 750000 and 268435456 bytes.");
    }

    [Theory(DisplayName = "Browser observability should deliver all signals when configured endpoint is relative or absolute")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BrowserObservability_Should_DeliverAllSignals_When_ConfiguredEndpointIsRelativeOrAbsolute(bool absolute, bool configureLimit)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.Logging.ClearProviders();
        await using var receiver = receiverBuilder.Build();
        receiver.Urls.Add("http://127.0.0.1:0");
        var received = new ConcurrentDictionary<string, byte[]>();
        var allSignals = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MapGet("/probe", (HttpContext context) => Results.Text(context.Request.Headers.TraceParent.ToString()));
        receiver.MapPost("/configured-telemetry/v1/{signal}", async (string signal, HttpContext context) =>
        {
            context.Request.ContentType.ShouldBe("application/x-protobuf");
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            received[signal] = body.ToArray();
            if (received.Count == 3)
            {
                allSignals.TrySetResult();
            }

            return Results.Bytes([], "application/x-protobuf");
        });
        await receiver.StartAsync(cancellationToken);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "browser-export-test",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = (absolute ? receiver.Urls.Single() + "/" : "") + "configured-telemetry/",
            ["OpenTelemetry:Otlp:MaxRequestSizeBytes"] = configureLimit ? "1048576" : null
        });
        builder.Services.AddBrowserObservability(new Uri(receiver.Urls.Single() + "/"), builder.Configuration);
        builder.Services.ShouldNotContain(service => service.ServiceType.FullName == "System.Net.Http.IHttpClientFactory");
        await using var application = builder.Build();
        var exporterOptions = application.Services.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>();
        foreach (var signal in new[] { "logs", "metrics", "traces" })
        {
            var options = exporterOptions.Get(signal);
            options.Endpoint.ShouldBe(new Uri(receiver.Urls.Single() + "/configured-telemetry/v1/" + signal));
            options.MaxRequestSizeBytes.ShouldBe(configureLimit ? 1048576 : 67108864);
        }
        var traces = application.Services.GetRequiredService<TracerProvider>();
        var metrics = application.Services.GetRequiredService<MeterProvider>();
        _ = application.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>().Single();
        application.Services.GetRequiredService<ITelemetryHostInitializer>().Initialize();
        using var source = new ActivitySource("Microsoft.AspNetCore.Components");
        using var meter = new Meter("Microsoft.AspNetCore.Components");
        using var customSource = new ActivitySource("unregistered-browser-probe");
        customSource.StartActivity("excluded").ShouldBeNull();

        using (source.StartActivity("client-action"))
        {
            var parent = Activity.Current.ShouldNotBeNull();
            using var client = new HttpClient();
            var traceparent = await client.GetStringAsync(new Uri(receiver.Urls.Single() + "/probe"), cancellationToken);
            ActivityContext.TryParse(traceparent, null, out var context).ShouldBeTrue();
            context.TraceId.ShouldBe(parent.TraceId);
            context.SpanId.ShouldNotBe(parent.SpanId);
            application.Logger.LogWarning("Browser export probe");
            meter.CreateCounter<long>("browser.actions").Add(1);
        }

        traces.ForceFlush(1000).ShouldBeTrue();
        metrics.ForceFlush(1000).ShouldBeTrue();
        await allSignals.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        foreach (string signal in new[] { "logs", "metrics", "traces" })
        {
            received[signal].ShouldNotBeEmpty();
            System.Text.Encoding.UTF8.GetString(received[signal]).ShouldContain("browser-export-test");
        }
    }
}
