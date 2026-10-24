using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;

using OpenTelemetry;

namespace PANiXiDA.Core.Observability.WebAssembly;

/// <summary>
/// Copies the SDK's serialized protobuf before its pooled buffer is reused, then
/// delivers it asynchronously without blocking the browser's single thread.
/// </summary>
internal sealed class BrowserOtlpTransport : HttpMessageHandler
{
    internal const int MaxPayloadBytes = 64 * 1024;
    private readonly Uri endpoint;
    private readonly HttpClient client;
    private readonly Channel<PendingExport> queue = Channel.CreateBounded<PendingExport>(64);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task delivery;
    private bool disposed;

    internal Task Completion => delivery;

    internal BrowserOtlpTransport(Uri endpoint, HttpMessageHandler? handler = null)
    {
        this.endpoint = endpoint;
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(5);
        delivery = DeliverAsync(stopping.Token);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(Enqueue(request, cancellationToken));

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        => Enqueue(request, cancellationToken);

    private HttpResponseMessage Enqueue(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri is not { } uri ||
            uri.GetLeftPart(UriPartial.Authority) != endpoint.GetLeftPart(UriPartial.Authority) ||
            !IsSignalPath(uri.AbsolutePath, endpoint.AbsolutePath) || request.Content is null ||
            request.Content.Headers.ContentLength is not (>= 0 and <= MaxPayloadBytes))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        // The standard OTLP exporter supplies in-memory protobuf content. Never
        // synchronously wait for an asynchronous operation in WebAssembly.
        var body = request.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!body.IsCompletedSuccessfully || body.Result.Length > MaxPayloadBytes ||
            !queue.Writer.TryWrite(new PendingExport(uri, body.Result.ToArray(), DateTimeOffset.UtcNow)))
        {
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };
    }

    private static bool IsSignalPath(string path, string prefix) =>
        path == prefix + "v1/traces" || path == prefix + "v1/metrics" || path == prefix + "v1/logs";

    private async Task DeliverAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(cancellationToken))
            {
                if (DateTimeOffset.UtcNow - item.CreatedAt > TimeSpan.FromSeconds(30))
                {
                    continue;
                }

                try
                {
                    using var suppression = SuppressInstrumentationScope.Begin();
                    using var request = new HttpRequestMessage(HttpMethod.Post, item.Endpoint)
                    {
                        Content = new ByteArrayContent(item.Payload)
                    };
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
                {
                    // Best-effort delivery: never log export failures back into
                    // the exporter or retain an unbounded offline backlog.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            stopping.Cancel();
            queue.Writer.TryComplete();
            client.Dispose();
            stopping.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record PendingExport(Uri Endpoint, byte[] Payload, DateTimeOffset CreatedAt);
}
