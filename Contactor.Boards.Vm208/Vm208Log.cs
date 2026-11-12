using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Vm208;

/// <summary>Log messages of the VM208 library.</summary>
internal static partial class Vm208Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "The VM208 at {Host}:{Port} answers")]
    public static partial void Connected(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the VM208: {Reason}")]
    public static partial void ConnectionLost(this ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the VM208 in {DelaySeconds} s")]
    public static partial void Reconnecting(this ILogger logger, string? delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "A VM208 subscriber failed")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM208 address set to {Host}:{Port}")]
    public static partial void EndpointChanged(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM208 simulator listening on 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(this ILogger logger, int port);
}
