using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Waveshare;

/// <summary>Log messages of the Waveshare library.</summary>
internal static partial class WaveshareLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "The Waveshare board at {Host}:{Port} answers")]
    public static partial void Connected(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the Waveshare board: {Reason}")]
    public static partial void ConnectionLost(this ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the Waveshare board in {DelaySeconds} s")]
    public static partial void Reconnecting(this ILogger logger, string? delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "A Waveshare subscriber failed")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waveshare address set to {Host}:{Port}")]
    public static partial void EndpointChanged(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Set the Waveshare board's relays to normal mode; they were {Modes}")]
    public static partial void RelayModesReset(this ILogger logger, string modes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not set the Waveshare board's relays to normal mode: {Reason}. Its inputs may switch relays past the app")]
    public static partial void RelayModesNotReset(this ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waveshare simulator listening on 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(this ILogger logger, int port);
}
