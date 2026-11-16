using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// Log messages of the DAEnetIP2 v2 client and simulator. None holds a community. Not extension methods, so they never
/// clash with the smartDEN's of the same name in this project.
/// </summary>
internal static partial class DaenetIp2Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "The DAEnetIP2 v2 board at {Host}:{Port} answers")]
    public static partial void Connected(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the DAEnetIP2 v2 board: {Reason}")]
    public static partial void ConnectionLost(ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Asking the DAEnetIP2 v2 board again in {DelaySeconds} s")]
    public static partial void Reconnecting(ILogger logger, string? delaySeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "DAEnetIP2 v2 address set to {Host}:{Port}")]
    public static partial void EndpointChanged(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "DAEnetIP2 v2 community {State}")]
    public static partial void CommunityChanged(ILogger logger, string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "A DAEnetIP2 v2 subscriber failed")]
    public static partial void SubscriberFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "DAEnetIP2 v2 simulator listening on UDP 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The DAEnetIP2 v2 simulator couldn't serve a datagram")]
    public static partial void SimulatorDatagramFailed(ILogger logger, Exception exception);
}
