using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Devantech;

/// <summary>Log messages of the Devantech library. None has the password.</summary>
internal static partial class DevantechLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "The Devantech board at {Host}:{Port} answers")]
    public static partial void Connected(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the Devantech board: {Reason}")]
    public static partial void ConnectionLost(this ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the Devantech board in {DelaySeconds} s")]
    public static partial void Reconnecting(this ILogger logger, string? delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Devantech board refused the password: not trying again until a new one is saved or a person checks again")]
    public static partial void Held(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "The Devantech board's analog inputs couldn't be read; the relays and inputs are read as usual: {Reason}")]
    public static partial void AnalogNotRead(this ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "A Devantech subscriber failed")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Devantech address set to {Host}:{Port}")]
    public static partial void EndpointChanged(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Devantech password changed; it is now {State}")]
    public static partial void PasswordChanged(this ILogger logger, string state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Devantech simulator listening on 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(this ILogger logger, int port);
}
