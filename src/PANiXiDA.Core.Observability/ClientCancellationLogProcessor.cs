using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using OpenTelemetry;
using OpenTelemetry.Logs;

namespace PANiXiDA.Core.Observability;

internal sealed class ClientCancellationLogProcessor(IHttpContextAccessor httpContextAccessor) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        if (data.LogLevel == LogLevel.Error
            && data.Exception is OperationCanceledException
            && httpContextAccessor.HttpContext?.RequestAborted.IsCancellationRequested == true)
        {
            data.LogLevel = LogLevel.Information;
        }
    }
}
