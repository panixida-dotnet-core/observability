using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace PANiXiDA.Core.Observability.WebAssembly.UnitTests;

public sealed class BrowserExportTests
{
    [Fact(DisplayName = "Browser observability should deliver protobuf for all signals asynchronously")]
    public async Task BrowserObservability_Should_DeliverAllSignals_When_ProvidersAreFlushed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var receiverBuilder = WebApplication.CreateSlimBuilder();
        receiverBuilder.Logging.ClearProviders();
        await using var receiver = receiverBuilder.Build();
        receiver.Urls.Add("http://127.0.0.1:0");
        var received = new ConcurrentDictionary<string, byte[]>();
        var allSignals = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        receiver.MapPost("/telemetry/v1/{signal}", async (string signal, HttpContext context) =>
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
        builder.Services.AddBrowserObservability(new Uri(receiver.Urls.Single() + "/telemetry/"), "browser-export-test");
        await using var application = builder.Build();
        var traces = application.Services.GetRequiredService<TracerProvider>();
        var metrics = application.Services.GetRequiredService<MeterProvider>();
        _ = application.Services.GetServices<ILoggerProvider>().OfType<OpenTelemetryLoggerProvider>().Single();
        _ = application.Services.GetRequiredService<BrowserOtlpTransport>();
        using var source = new ActivitySource("browser-export-probe");
        using var meter = new Meter("browser-export-probe");

        using (source.StartActivity("client-action"))
        {
            application.Logger.LogWarning("Browser export probe");
            meter.CreateCounter<long>("browser.actions").Add(1);
        }

        traces.ForceFlush(1000).ShouldBeTrue();
        metrics.ForceFlush(1000).ShouldBeTrue();
        await allSignals.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        foreach (string signal in new[] { "logs", "metrics", "traces" })
        {
            received[signal].ShouldNotBeEmpty();
        }
    }
}
