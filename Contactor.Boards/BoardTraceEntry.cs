namespace Contactor.Boards;

/// <summary>Which way a step on the board connection went.</summary>
public enum BoardTraceDirection
{
    /// <summary>A connection step, not a message: connecting, lost, waiting.</summary>
    None,

    /// <summary>A message to the board.</summary>
    Sent,

    /// <summary>A message from the board.</summary>
    Received,
}

/// <summary>
/// One step on the board connection, in plain words, for the Debug screen and the traffic toasts. Each board turns its
/// own messages into these, and never puts a password in one.
/// </summary>
/// <param name="At">When it happened.</param>
/// <param name="Direction">To the board, from it, or neither.</param>
/// <param name="Text">In plain words, e.g. "Turn on relay 3".</param>
/// <param name="Bytes">The message's bytes as text (e.g. hex), or null when there is no message.</param>
/// <param name="IsError">Shown in red.</param>
/// <param name="IsRoutine">No toast: the board's keep-alive and other messages it sends on every connection.</param>
public sealed record BoardTraceEntry(
    DateTimeOffset At,
    BoardTraceDirection Direction,
    string Text,
    string? Bytes,
    bool IsError,
    bool IsRoutine);

/// <summary>A board's traffic, step by step, for the Debug screen.</summary>
public interface IBoardTraffic
{
    /// <summary>Raised on every step, on the connection's own loop: handlers must be quick and not throw.</summary>
    event EventHandler<BoardTraceEntry>? Traced;
}
