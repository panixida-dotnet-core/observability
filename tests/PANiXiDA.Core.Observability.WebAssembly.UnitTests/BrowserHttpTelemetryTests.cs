using System.Diagnostics;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace PANiXiDA.Core.Observability.WebAssembly.UnitTests;

public sealed class BrowserHttpTelemetryTests
{
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
