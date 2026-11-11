using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Vm201;

/// <summary>
/// Log messages of the VM201 library. Never add the password to any of them.
/// </summary>
internal static partial class Vm201Log
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Connecting to the VM201 at {Host}:{Port}")]
    public static partial void Connecting(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The VM201 requires a login")]
    public static partial void LoginRequired(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Logged in to the VM201 at {Host}:{Port}")]
    public static partial void LoggedIn(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "The VM201 rejected the user name or password")]
    public static partial void AccessDenied(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The VM201 closed the connection")]
    public static partial void ClosedByBoard(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the VM201: {Reason}")]
    public static partial void ConnectionLost(this ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error on the VM201 connection")]
    public static partial void UnexpectedError(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the VM201 in {DelaySeconds} s")]
    public static partial void Reconnecting(this ILogger logger, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored VM201 packet {Command}")]
    public static partial void IgnoredPacket(this ILogger logger, char command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored VM201 status with {ByteCount} data bytes")]
    public static partial void InvalidStatus(this ILogger logger, int byteCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "A VM201 state subscriber failed")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarded {ByteCount} invalid bytes from the VM201 stream")]
    public static partial void DiscardedBytes(this ILogger logger, int byteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 simulator listening on 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(this ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 → {Packet}")]
    public static partial void PacketSent(this ILogger logger, string packet);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 ← {Packet}")]
    public static partial void PacketReceived(this ILogger logger, string packet);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 address set to {Host}:{Port}")]
    public static partial void EndpointChanged(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 login changed (none: {None})")]
    public static partial void LoginChanged(this ILogger logger, bool none);
}
