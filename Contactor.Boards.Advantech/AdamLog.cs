using System.Net;
using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Advantech;

/// <summary>Log messages of the ADAM client and simulator.</summary>
internal static partial class AdamLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "The ADAM module at {Host}:{Port} answers")]
    public static partial void Connected(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the ADAM module: {Reason}")]
    public static partial void ConnectionLost(this ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the ADAM module in {DelaySeconds} s")]
    public static partial void Reconnecting(this ILogger logger, string? delaySeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "ADAM module address set to {Host}:{Port}")]
    public static partial void EndpointChanged(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "An ADAM module subscriber failed")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "ADAM simulator listening on {Address}:{Port}")]
    public static partial void SimulatorListening(this ILogger logger, IPAddress address, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The ADAM simulator closed a connection it couldn't serve")]
    public static partial void SimulatorConnectionFailed(this ILogger logger, Exception exception);
}
