using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Trace;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class SignalRTelemetryTests
{
    [Theory(DisplayName = "AddObservability exports real SignalR hub calls including failed operations without explicit source registration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddObservability_ShouldExportSignalRActivities(bool fail)
    {
        const string sourceName = "Microsoft.AspNetCore.SignalR.Server";
        var method = fail ? "Fail" : "Echo";
        var activities = new ConcurrentQueue<Activity>();
        var completed = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var exporter = new RecordingExporter(activity =>
        {
            if (activity.Source.Name == sourceName && Equals(activity.GetTagItem("rpc.service"), typeof(TelemetryHub).FullName))
            {
                activities.Enqueue(activity);
                if (Equals(activity.GetTagItem("rpc.method"), method))
                {
                    completed.TrySetResult(activity);
                }
            }
        });
        using var processor = new SimpleActivityExportProcessor(exporter);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
            ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1"
        });
        builder.AddObservability();
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(processor));
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.MapHub<TelemetryHub>("/telemetry");
        await app.StartAsync(TestContext.Current.CancellationToken);
        await using var connection = new HubConnectionBuilder().WithUrl($"{app.Urls.Single()}/telemetry", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.WebSocketConfiguration = socket => socket.Proxy = null;
        }).Build();

        await connection.StartAsync(TestContext.Current.CancellationToken);
        if (fail)
        {
            await Should.ThrowAsync<HubException>(() => connection.InvokeAsync("Fail", TestContext.Current.CancellationToken));
        }
        else
        {
            (await connection.InvokeAsync<string>("Echo", "private-payload", TestContext.Current.CancellationToken)).ShouldBe("private-payload");
        }
        var activity = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await connection.StopAsync(TestContext.Current.CancellationToken);

        activity.Source.Name.ShouldBe(sourceName);
        activity.GetTagItem("rpc.system").ShouldBe("signalr");
        activity.GetTagItem("rpc.method").ShouldBe(method);
        activity.Status.ShouldBe(fail ? ActivityStatusCode.Error : ActivityStatusCode.Unset);
        activities.Count(candidate => ReferenceEquals(candidate, activity)).ShouldBe(1);
        activities.ShouldContain(candidate => Equals(candidate.GetTagItem("rpc.method"), "OnConnectedAsync"));
        activity.TagObjects.Select(tag => tag.Value).ShouldNotContain("private-payload");
    }

    public sealed class TelemetryHub : Hub
    {
        public string Echo(string value) => value;
        public void Fail() => throw new InvalidOperationException("Expected hub failure");
    }

    private sealed class RecordingExporter(Action<Activity> record) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) { record(activity); }
            return ExportResult.Success;
        }
    }
}
