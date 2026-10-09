namespace PANiXiDA.Core.Observability;

internal static class CancellationExceptionDetector
{
    internal static bool IsCancellation(Exception? exception)
    {
        if (exception is OperationCanceledException)
        {
            return true;
        }

        if (exception is not AggregateException)
        {
            return false;
        }

        var pending = new Stack<Exception>();
        pending.Push(exception);

        while (pending.TryPop(out var current))
        {
            switch (current)
            {
                case OperationCanceledException:
                    break;
                case AggregateException { InnerExceptions.Count: > 0 } aggregate:
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        pending.Push(inner);
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
