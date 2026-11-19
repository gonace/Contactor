using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Velleman;

/// <summary>
/// Log messages of the VM201 library. Never add the password to any of them.
/// </summary>
internal static partial class Vm201Log
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Connecting to the VM201 at {Host}:{Port}")]
    public static partial void Connecting(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The VM201 requires a login")]
    public static partial void LoginRequired(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Logged in to the VM201 at {Host}:{Port}")]
    public static partial void LoggedIn(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "The VM201 rejected the user name or password")]
    public static partial void AccessDenied(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The VM201 refused the login: not trying again until a new one is saved or a person checks again")]
    public static partial void Held(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The VM201 closed the connection")]
    public static partial void ClosedByBoard(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the VM201: {Reason}")]
    public static partial void ConnectionLost(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error on the VM201 connection")]
    public static partial void UnexpectedError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the VM201 in {DelaySeconds} s")]
    public static partial void Reconnecting(ILogger logger, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored VM201 packet {Command}")]
    public static partial void IgnoredPacket(ILogger logger, char command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignored VM201 status with {ByteCount} data bytes")]
    public static partial void InvalidStatus(ILogger logger, int byteCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "A VM201 state subscriber failed")]
    public static partial void SubscriberFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Discarded {ByteCount} invalid bytes from the VM201 stream")]
    public static partial void DiscardedBytes(ILogger logger, int byteCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 simulator listening on 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 → {Packet}")]
    public static partial void PacketSent(ILogger logger, string packet);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 ← {Packet}")]
    public static partial void PacketReceived(ILogger logger, string packet);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 address set to {Host}:{Port}")]
    public static partial void EndpointChanged(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM201 login changed (none: {None})")]
    public static partial void LoginChanged(ILogger logger, bool none);
}
