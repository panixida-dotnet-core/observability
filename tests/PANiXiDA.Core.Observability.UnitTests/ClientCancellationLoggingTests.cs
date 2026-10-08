using System.Diagnostics;
using System.Runtime.ExceptionServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace PANiXiDA.Core.Observability.UnitTests;

public sealed class ClientCancellationLoggingTests
{
    private static readonly EventId InvocationFailedEvent = new(42, "InvocationFailed");

    [Theory(DisplayName = "AddObservability only downgrades errors for an aborted request and a cancellation exception")]
    [InlineData(ExceptionKind.OperationCanceled, true, true, LogLevel.Error, LogLevel.Information)]
    [InlineData(ExceptionKind.TaskCanceled, true, true, LogLevel.Error, LogLevel.Information)]
    [InlineData(ExceptionKind.DatabaseCancellation, true, true, LogLevel.Error, LogLevel.Information)]
    [InlineData(ExceptionKind.OperationCanceled, true, false, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.TaskCanceled, true, false, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.OperationCanceled, false, false, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.None, true, true, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.DatabaseTimeout, true, false, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.DatabaseTimeout, true, true, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.PostgresCancellation, true, true, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.ApplicationFailure, true, true, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.WrappedCancellation, true, true, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.MixedAggregate, true, true, LogLevel.Error, LogLevel.Error)]
    [InlineData(ExceptionKind.OperationCanceled, true, true, LogLevel.Warning, LogLevel.Warning)]
    [InlineData(ExceptionKind.OperationCanceled, true, true, LogLevel.Critical, LogLevel.Critical)]
    public void AddObservability_ShouldClassifyCancellation(
        ExceptionKind exceptionKind,
        bool hasHttpContext,
        bool requestAborted,
        LogLevel originalLevel,
        LogLevel expectedLevel)
    {
        var exception = CreateException(exceptionKind);

        var record = ExportLog(exception, hasHttpContext, requestAborted, originalLevel);

        record.Level.Should().Be(expectedLevel);
        record.Exception.Should().BeSameAs(exception);
    }

    [Fact(DisplayName = "AddObservability retains cancellation details and correlation through batch export")]
    public void AddObservability_ShouldPreserveCancellationDetails()
    {
        var exception = ExceptionDispatchInfo.SetCurrentStackTrace(new TaskCanceledException("Request canceled"));
        using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();

        var record = ExportLog(exception, true, true, LogLevel.Error);

        record.Level.Should().Be(LogLevel.Information);
        record.Exception.Should().BeSameAs(exception);
        record.Exception!.StackTrace.Should().NotBeNullOrEmpty();
        record.Message.Should().Be("Invocation of GetHeroesQuery failed!");
        record.Category.Should().Be("GetHeroesQuery");
        record.EventId.Should().Be(InvocationFailedEvent);
        record.Attributes["Message"].Should().Be("GetHeroesQuery");
        record.Scopes["RequestId"].Should().Be("request-42");
        record.TraceId.Should().Be(activity.TraceId);
        record.SpanId.Should().Be(activity.SpanId);
    }

    private static ExportedLog ExportLog(Exception? exception, bool hasHttpContext, bool requestAborted, LogLevel level)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_EXPORTER_OTLP_TIMEOUT"] = "1"
        });
        builder.AddObservability();
        using var exporter = new RecordingExporter();
        using var batchProcessor = new BatchLogRecordExportProcessor(exporter, scheduledDelayMilliseconds: 60000);
        builder.Services.AddOpenTelemetry().WithLogging(logging => logging.AddProcessor(batchProcessor));
        using var app = builder.Build();
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = hasHttpContext
            ? new DefaultHttpContext { RequestAborted = new CancellationToken(requestAborted) }
            : null;

        try
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GetHeroesQuery");
            using (logger.BeginScope(new Dictionary<string, object?> { ["RequestId"] = "request-42" }))
            {
                if (logger.IsEnabled(level))
                {
                    logger.Log(level, InvocationFailedEvent, exception, "Invocation of {Message} failed!", "GetHeroesQuery");
                }
            }
        }
        finally
        {
            accessor.HttpContext = null;
        }

        batchProcessor.ForceFlush().Should().BeTrue();
        return exporter.Records.Should().ContainSingle().Subject;
    }

    private static Exception? CreateException(ExceptionKind kind)
    {
        return kind switch
        {
            ExceptionKind.None => null,
            ExceptionKind.OperationCanceled => new OperationCanceledException(),
            ExceptionKind.TaskCanceled => new TaskCanceledException(),
            ExceptionKind.DatabaseCancellation => new OperationCanceledException(
                "Query was cancelled", new PostgresException("canceling statement due to user request", "ERROR", "ERROR", "57014")),
            ExceptionKind.DatabaseTimeout => new NpgsqlException("Exception while reading from stream", new TimeoutException()),
            ExceptionKind.PostgresCancellation => new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", "57014"),
            ExceptionKind.ApplicationFailure => new InvalidOperationException("Independent failure"),
            ExceptionKind.WrappedCancellation => new InvalidOperationException("Wrapper", new OperationCanceledException()),
            ExceptionKind.MixedAggregate => new AggregateException(new OperationCanceledException(), new InvalidOperationException()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    public enum ExceptionKind
    {
        None,
        OperationCanceled,
        TaskCanceled,
        DatabaseCancellation,
        DatabaseTimeout,
        PostgresCancellation,
        ApplicationFailure,
        WrappedCancellation,
        MixedAggregate
    }

    private sealed class RecordingExporter : BaseExporter<LogRecord>
    {
        public List<ExportedLog> Records { get; } = [];

        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch)
            {
                var scopes = new Dictionary<string, object?>();
                record.ForEachScope(static (scope, values) =>
                {
                    foreach (var pair in scope)
                    {
                        values[pair.Key] = pair.Value;
                    }
                }, scopes);
                Records.Add(new ExportedLog(record.LogLevel, record.Exception, record.FormattedMessage,
                    record.CategoryName, record.EventId, record.Attributes!.ToDictionary(), scopes, record.TraceId, record.SpanId));
            }

            return ExportResult.Success;
        }
    }

    private sealed record ExportedLog(
        LogLevel Level,
        Exception? Exception,
        string? Message,
        string? Category,
        EventId EventId,
        Dictionary<string, object?> Attributes,
        Dictionary<string, object?> Scopes,
        ActivityTraceId TraceId,
        ActivitySpanId SpanId);
}
