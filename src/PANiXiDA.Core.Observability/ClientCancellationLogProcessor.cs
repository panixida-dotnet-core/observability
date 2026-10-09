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
            && httpContextAccessor.HttpContext?.RequestAborted.IsCancellationRequested == true
            && CancellationExceptionDetector.IsCancellation(data.Exception))
        {
            data.LogLevel = LogLevel.Information;
        }
    }
}
