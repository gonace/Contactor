using Microsoft.Extensions.Logging;

namespace Contactor.Boards.Vm204;

/// <summary>Log messages of the VM204 library. Never add the password or the key to any of them.</summary>
internal static partial class Vm204Log
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Logged in to the VM204 at {Host}:{Port}")]
    public static partial void LoggedIn(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Error, Message = "The VM204 refused the user name or password")]
    public static partial void AccessDenied(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the VM204: {Reason}")]
    public static partial void ConnectionLost(this ILogger logger, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnecting to the VM204 in {DelaySeconds} s")]
    public static partial void Reconnecting(this ILogger logger, string? delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "A VM204 subscriber failed")]
    public static partial void SubscriberFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM204 address set to {Host}:{Port}")]
    public static partial void EndpointChanged(this ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM204 login changed (none: {None})")]
    public static partial void LoginChanged(this ILogger logger, bool none);

    [LoggerMessage(Level = LogLevel.Information, Message = "VM204 simulator listening on 127.0.0.1:{Port}")]
    public static partial void SimulatorListening(this ILogger logger, int port);
}
