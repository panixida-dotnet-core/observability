using System.Net;

namespace PANiXiDA.Core.Observability.WebAssembly.UnitTests;

public sealed class BrowserOtlpTransportTests
{
    private static readonly Uri Endpoint = new("https://admin.example.test/telemetry/");

    [Theory(DisplayName = "Export delivery should continue after a network failure without a recursive log")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_Should_ContinueDelivery_When_PreviousRequestFailed(bool canceled)
    {
        int calls = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CaptureHandler((_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromException<HttpResponseMessage>(canceled ? new OperationCanceledException() : new HttpRequestException("synthetic failure"));
            }

            delivered.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var transport = new BrowserOtlpTransport(Endpoint, handler);
        using var client = new HttpClient(transport, disposeHandler: false);
        using var first = await client.PostAsync(new Uri(Endpoint, "v1/logs"), new ByteArrayContent([1]), TestContext.Current.CancellationToken);
        using var second = await client.PostAsync(new Uri(Endpoint, "v1/logs"), new ByteArrayContent([2]), TestContext.Current.CancellationToken);

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        calls.ShouldBe(2);
        transport.Dispose();
        await transport.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "Export should return before the network completes and copy the SDK buffer")]
    public async Task Export_Should_ReturnBeforeNetworkCompletes_When_TransportIsSlow()
    {
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new CaptureHandler(async (request, cancellationToken) =>
        {
            received.TrySetResult(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var transport = new BrowserOtlpTransport(Endpoint, handler);
        using var client = new HttpClient(transport, disposeHandler: false);
        byte[] payload = [1, 2, 3];
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "v1/traces"))
        {
            Content = new ByteArrayContent(payload)
        };

        var sending = client.SendAsync(request, TestContext.Current.CancellationToken);
        sending.IsCompletedSuccessfully.ShouldBeTrue();
        using var response = await sending;
        payload[0] = 9;
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        actual.ShouldBe(new byte[] { 1, 2, 3 });
        release.TrySetResult();
    }

    [Theory(DisplayName = "Export should reject requests outside the configured signal endpoints")]
    [InlineData("https://external.example.test/telemetry/v1/logs", 1)]
    [InlineData("https://admin.example.test/telemetry/v1/secrets", 1)]
    [InlineData("https://admin.example.test/telemetry/v1/metrics", 65537)]
    public async Task Export_Should_RejectUnsupportedRequests_When_EndpointOrSizeIsInvalid(string url, int size)
    {
        using var transport = new BrowserOtlpTransport(Endpoint);
        using var client = new HttpClient(transport, disposeHandler: false);
        using var response = await client.PostAsync(url, new ByteArrayContent(new byte[size]), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Export should bound the queue when the collector is stalled")]
    public async Task Export_Should_BoundQueue_When_CollectorIsStalled()
    {
        using var handler = new CaptureHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var transport = new BrowserOtlpTransport(Endpoint, handler);
        using var client = new HttpClient(transport, disposeHandler: false);
        int rejected = 0;
        for (int index = 0; index < 100; index++)
        {
            using var response = await client.PostAsync(new Uri(Endpoint, "v1/logs"), new ByteArrayContent([1]), TestContext.Current.CancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected++;
            }
        }

        rejected.ShouldBeGreaterThanOrEqualTo(35);
    }

    internal sealed class CaptureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
