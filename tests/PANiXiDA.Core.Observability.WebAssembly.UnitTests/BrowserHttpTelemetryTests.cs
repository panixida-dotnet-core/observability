using System.Diagnostics;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace PANiXiDA.Core.Observability.WebAssembly.UnitTests;

public sealed class BrowserHttpTelemetryTests
{
    [Theory(DisplayName = "HTTP instrumentation should retain error status and propagate network failures")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpInstrumentation_Should_RetainErrorStatus_When_NetworkRequestFails(bool canceled)
    {
        Activity? stopped = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "PANiXiDA.Core.Observability.WebAssembly.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped = activity
        };
        ActivitySource.AddActivityListener(listener);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new BrowserHttpTelemetryFilter(new Uri("https://admin.example.test/telemetry/")));
        services.AddHttpClient("failure").ConfigurePrimaryHttpMessageHandler(() => new BrowserOtlpTransportTests.CaptureHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(canceled ? new OperationCanceledException() : new HttpRequestException("synthetic failure"))));
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("failure");

        await Should.ThrowAsync<Exception>(() => client.GetAsync("https://admin.example.test/api/probe", TestContext.Current.CancellationToken));

        stopped.ShouldNotBeNull();
        stopped.Status.ShouldBe(ActivityStatusCode.Error);
        stopped.GetTagItem("error.type").ShouldBe(canceled ? typeof(OperationCanceledException).FullName : typeof(HttpRequestException).FullName);
    }

    [Theory(DisplayName = "HTTP instrumentation should propagate trace context only to the application origin")]
    [InlineData("https://admin.example.test/api/probe", true)]
    [InlineData("https://external.example.test/api/probe", false)]
    public async Task HttpInstrumentation_Should_PropagateTraceContext_When_RequestIsSameOrigin(string destination, bool expected)
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "PANiXiDA.Core.Observability.WebAssembly.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        string? traceparent = null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new BrowserHttpTelemetryFilter(new Uri("https://admin.example.test/telemetry/")));
        services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new BrowserOtlpTransportTests.CaptureHandler((request, _) =>
        {
            traceparent = request.Headers.TryGetValues("traceparent", out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("probe");

        using var response = await client.GetAsync(destination, TestContext.Current.CancellationToken);

        (traceparent is not null).ShouldBe(expected);
        if (expected)
        {
            ActivityContext.TryParse(traceparent, null, out _).ShouldBeTrue();
        }
    }
}
