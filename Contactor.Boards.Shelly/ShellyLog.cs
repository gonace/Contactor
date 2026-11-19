using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Shelly;

/// <summary>Log messages of the Shelly library. None has the password or a digest.</summary>
internal static partial class ShellyLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "The Shelly at {Host}:{Port} answers")]
    public static partial void Connected(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Shelly refused the password, or asks for one")]
    public static partial void AccessDenied(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the Shelly: {Reason}")]
    public static partial void ConnectionLost(this ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the Shelly in {DelaySeconds} s")]
    public static partial void Reconnecting(this ILogger logger, string? delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Shelly refused the password: not trying again until a new one is saved or a person checks again")]
    public static partial void Held(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "A Shelly subscriber failed")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelly address set to {Host}:{Port}")]
    public static partial void EndpointChanged(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelly password changed; it is now {State}")]
    public static partial void PasswordChanged(this ILogger logger, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shelly simulator listening on 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(this ILogger logger, int port);
}
