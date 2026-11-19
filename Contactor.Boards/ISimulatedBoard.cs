namespace Contactor.Boards;

/// <summary>
/// A fake board for development and tests, listening on a local port as the real model would: TCP, or UDP for an SNMP
/// board. Each board model's project brings its own. It is driven from the outside as the board itself would be, e.g. a
/// relay switched by its own button, and reports every message to and from the app.
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

    /// <summary>
    /// Sets what an analog input reads, in counts of the board's converter (0 to
    /// <see cref="AnalogAbility.MaxCounts"/>), and tells the app if anything changed. Throws for an input the board
    /// doesn't simulate (<see cref="SimulatedBoardState.Analog"/>) or a count out of range.
    /// </summary>
    Task SetAnalogAsync(int input, int counts)
    {
        throw new ArgumentOutOfRangeException(nameof(input), input, "This board has no analog inputs to set.");
    }

    /// <summary>Closes every open connection, as if the network dropped. A UDP board has none: it forgets its clients.</summary>
    void DropConnections();
}

/// <summary>A simulated board's state, a snapshot. Two snapshots compare their client lists by reference.</summary>
/// <param name="Relays">The relays that are on.</param>
/// <param name="Inputs">The inputs that are on (closed).</param>
/// <param name="Clients">Where each open connection comes from, e.g. "127.0.0.1:52144".</param>
public sealed record SimulatedBoardState(ChannelMask Relays, ChannelMask Inputs, IReadOnlyList<string> Clients)
{
    /// <summary>
    /// What each analog input reads, input 1 first, in counts of the board's converter; empty when the board has none,
    /// or doesn't simulate them. Compared by reference, as <see cref="Clients"/>.
    /// </summary>
    public IReadOnlyList<int> Analog { get; init; } = [];
}

/// <summary>A simulated board with timer programs of its own, which the app may turn off.</summary>
public interface ISimulatedBoardTimers
{
    /// <summary>The relays whose own timer program is on.</summary>
    ChannelMask Timers { get; }

    /// <summary>Turns timer programs on or off as the board's own web page would, and tells the app if anything changed.</summary>
    Task SetTimersAsync(ChannelMask timers);

    /// <summary>
    /// Turns one relay's timer program on or off as the board's own web page would, leaving the others as they are, and
    /// tells the app if anything changed. Throws <see cref="ArgumentOutOfRangeException"/> for a relay the board doesn't have.
    /// </summary>
    Task SetTimerAsync(int relay, bool turnOn);
}

/// <summary>
/// A simulated board whose relays each have a control mode of the board's own, e.g. the Waveshare (B)'s linkage, which
/// the app may set back to normal.
/// </summary>
public interface ISimulatedBoardRelayModes
{
    /// <summary>Each mode's name, by its number: index 0 is the normal mode.</summary>
    IReadOnlyList<string> ModeNames { get; }

    /// <summary>The mode of each relay that has one, relay 1 first.</summary>
    IReadOnlyList<int> Modes { get; }

    /// <summary>
    /// Sets one relay's mode as the board's own tool or web page would, leaving the others as they are, and tells the
    /// app if anything changed. Throws <see cref="ArgumentOutOfRangeException"/> for a relay without a mode or a mode it
    /// doesn't have.
    /// </summary>
    Task SetModeAsync(int relay, int mode);
}

/// <summary>A simulated board on TCP that can be unreachable.</summary>
public interface ISimulatedBoardRejectConnections
{
    /// <summary>When true, new connections are closed at once, as if the board were unreachable.</summary>
    bool RejectConnections { get; set; }
}

/// <summary>A simulated board that can hang: it reads the app's requests and never answers them.</summary>
public interface ISimulatedBoardNoAnswer
{
    /// <summary>When true, the board reads every request and answers none, keeping the connection open.</summary>
    bool NoAnswer { get; set; }
}

/// <summary>A simulated board that can refuse to switch a relay, as the real one answers an error.</summary>
public interface ISimulatedBoardFailRelays
{
    /// <summary>The relays (bit 0 = relay 1) whose switch command gets the board's error answer and isn't carried out.</summary>
    ulong FailRelays { get; set; }
}

/// <summary>A simulated board that pulses relays (<see cref="SimulatedPulses"/>).</summary>
public interface ISimulatedBoardPulses
{
    /// <summary>The relays whose pulse is still running.</summary>
    ChannelMask Pulsing { get; }
}

/// <summary>
/// A simulated board whose power can be cut, as a power cut or a restart would: while it is off, nothing answers the app
/// and every relay is off; when the power comes back, each relay is as the board sets it after a power cut, as its
/// manual says (or, where the manual doesn't say, as its README states the simulator assumes).
/// </summary>
public interface ISimulatedBoardPower
{
    /// <summary>Whether the power is cut.</summary>
    bool IsPoweredOff { get; }

    /// <summary>What the board does when the power comes back, in words for the page, e.g. "Every relay comes back off.".</summary>
    string AfterPowerCut { get; }

    /// <summary>
    /// Cuts the power: open connections drop, new ones get nothing, running pulses end and every relay goes off. Nothing
    /// while it is cut already.
    /// </summary>
    Task CutPowerAsync();

    /// <summary>Gives the power back: each relay as the board sets it after a power cut, and it answers again. Nothing while it is on.</summary>
    Task RestorePowerAsync();
}

/// <summary>
/// One of a simulated board's own rules that the page can run now, as when its time comes: a timer program, a schedule
/// or an auto-on. The board switches its relays as the rule says, and the app sees a change it didn't ask for.
/// </summary>
/// <param name="Id">Its id on this board, e.g. "timer-3-on".</param>
/// <param name="Name">What it is, in words for the page, e.g. "Relay 3's timer: on".</param>
/// <param name="Switches">What it does: each relay it switches, and whether on.</param>
public sealed record SimulatedRule(string Id, string Name, IReadOnlyList<SimulatedRuleSwitch> Switches);

/// <summary>One relay a rule switches.</summary>
public readonly record struct SimulatedRuleSwitch(int Relay, bool TurnOn);

/// <summary>A simulated board whose own rules the page can run now.</summary>
public interface ISimulatedBoardRules
{
    /// <summary>The rules that are on, which the board would run when their time comes, in the board's order.</summary>
    IReadOnlyList<SimulatedRule> RunnableRules { get; }

    /// <summary>
    /// Runs a rule now: switches its relays as it says, as the board's own switch would, and tells the app if anything
    /// changed. Returns the rule it ran, or null for an id that isn't among <see cref="RunnableRules"/> (any more).
    /// </summary>
    Task<SimulatedRule?> RunRuleAsync(string id);
}
