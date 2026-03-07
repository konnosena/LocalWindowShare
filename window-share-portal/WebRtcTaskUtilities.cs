using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;

internal static class WebRtcTaskUtilities
{
    public static void StartObservedBackgroundTask(Func<Task> work, ILogger logger, string description)
    {
        var task = Task.Run(work);
        task.ContinueWith(
            static (completedTask, state) =>
            {
                _ = completedTask.Exception;
                if (!completedTask.IsFaulted || completedTask.Exception is null)
                {
                    return;
                }

                var (logger, description) = ((ILogger Logger, string Description))state!;
                var exception = completedTask.Exception.Flatten();
                if (IsExpectedSocketShutdown(exception))
                {
                    logger.LogDebug("Ignored expected background shutdown during {Description}.", description);
                    return;
                }

                logger.LogWarning(exception, "Background WebRTC task failed during {Description}.", description);
            },
            (logger, description),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public static bool IsExpectedSocketShutdown(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => true,
            ObjectDisposedException => true,
            IOException ioException when ioException.InnerException is Exception innerException => IsExpectedSocketShutdown(innerException),
            WebSocketException webSocketException => webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely ||
                                                    webSocketException.InnerException is Exception innerException && IsExpectedSocketShutdown(innerException),
            SocketException socketException => IsExpectedSocketError(socketException.SocketErrorCode) || socketException.ErrorCode == 995,
            AggregateException aggregateException => aggregateException.InnerExceptions.All(IsExpectedSocketShutdown),
            _ => false,
        };
    }

    private static bool IsExpectedSocketError(SocketError socketError)
    {
        return socketError is SocketError.OperationAborted
            or SocketError.ConnectionAborted
            or SocketError.ConnectionReset
            or SocketError.Shutdown
            or SocketError.Interrupted;
    }
}
