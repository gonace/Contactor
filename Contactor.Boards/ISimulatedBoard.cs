namespace Contactor.Boards;

/// <summary>
/// A fake board for development and tests, listening on a local TCP port as the real model would. Each board model's
/// project brings its own. It is driven from the outside as the board itself would be, e.g. a relay switched by its
/// own button, and reports every message to and from the app.
/// </summary>
public interface ISimulatedBoard : IAsyncDisposable
{
    /// <summary>The model it pretends to be.</summary>
    BoardModel Model { get; }

    /// <summary>The port it listens on, after <see cref="Start"/>; 0 before.</summary>
    int Port { get; }

    /// <summary>Whether it asks for a login.</summary>
    bool HasLogin { get; }

    /// <summary>Its relays and inputs, and who is connected.</summary>
    SimulatedBoardState State { get; }

    /// <summary>Raised when <see cref="State"/> changes, from any thread. A handler must be quick.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Raised on every step: a message from the app (<see cref="BoardTraceDirection.Sent"/>, to the board), one to the
    /// app (<see cref="BoardTraceDirection.Received"/>) or a connection step. From any thread, sometimes while the board
    /// holds its own lock: a handler must be quick and must never wait on the board. Login messages never show the
    /// password.
    /// </summary>
    event EventHandler<BoardTraceEntry>? Traced;

    /// <summary>Starts listening on 127.0.0.1. Port 0 picks a free port. Throws when the port is taken.</summary>
    void Start(int port = 0);

    /// <summary>Switches a relay as the board's own button would, and tells the app if anything changed.</summary>
    Task SwitchRelayAsync(int relay, bool turnOn);

    /// <summary>Sets a digital input, and tells the app if anything changed.</summary>
    Task SetInputAsync(int input, bool closed);

    /// <summary>Closes every open connection, as if the network dropped.</summary>
    void DropConnections();
}

/// <summary>A simulated board's state, a snapshot. Two snapshots compare their client lists by reference.</summary>
/// <param name="Relays">The relays that are on.</param>
/// <param name="Inputs">The inputs that are on (closed).</param>
/// <param name="Clients">Where each open connection comes from, e.g. "127.0.0.1:52144".</param>
public sealed record SimulatedBoardState(ChannelMask Relays, ChannelMask Inputs, IReadOnlyList<string> Clients);

/// <summary>A simulated board with timer programs of its own, which the app may turn off.</summary>
public interface ISimulatedBoardTimers
{
    /// <summary>The relays whose own timer program is on.</summary>
    ChannelMask Timers { get; }

    /// <summary>Turns timer programs on or off as the board's own web page would, and tells the app if anything changed.</summary>
    Task SetTimersAsync(ChannelMask timers);
}
