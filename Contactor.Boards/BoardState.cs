namespace Contactor.Boards;

/// <summary>
/// A board's last reported state. The board is the source of truth: the app never stores which relays are on. While
/// disconnected, the last known values are kept with <see cref="IsConnected"/> false, so the screens can show them
/// with a warning.
/// </summary>
/// <param name="IsConnected">Connected, and the board has reported its state on the current connection.</param>
/// <param name="Relays">The relays that are on.</param>
/// <param name="Inputs">The digital inputs that are on.</param>
/// <param name="LastContactUtc">When the board last reported its state, or null if it never has.</param>
/// <param name="Problem">
/// Why the board refused the last connection on purpose, e.g. another model or a wrong password; null while it answers,
/// and when it doesn't answer at all or can't tell.
/// </param>
/// <param name="Analog">
/// What the analog inputs last read, kept while disconnected as the relays are; null for a board without analog inputs
/// or before it answered. A change in it alone comes as <see cref="IRelayBoard.AnalogChanged"/>, never as
/// <see cref="IRelayBoard.StateChanged"/>.
/// </param>
/// <param name="OwnRules">
/// The rules of its own the board last reported (<see cref="OwnRulesAbility.Reads"/>), kept while disconnected as the
/// relays are; empty when it has none, and null for a board that can't read them or before it reported them. A change in
/// it alone comes as <see cref="IRelayBoard.OwnRulesChanged"/>. A board that reads them tries to before it reports its
/// first connected state on each connection, so null while <see cref="IsConnected"/> means it couldn't read them (another
/// firmware, a refusal, an answer it can't read), and the screens say so (issue #551).
/// </param>
/// <param name="PowerOn">
/// Each relay's state after a power cut as the board last reported it (<see cref="BoardCapabilities.PowerOn"/>), kept
/// while disconnected as the relays are; null for a board that can't read it or before it reported it. A change in it
/// alone comes as <see cref="IRelayBoard.PowerOnChanged"/>.
/// </param>
/// <param name="KeptUnavailable">
/// While <see cref="OwnRules"/> is null: the relays the board last showed it controls by itself
/// (<see cref="Boards.OwnRules.Unavailable"/>), from its last read of its rules that worked (issue #567). A read that
/// fails says nothing about them, so they stay unavailable until a read shows them otherwise; never more relays than that
/// read showed. None for a board that never read them, and once its state is forgotten (another address or model).
/// Ignored while <see cref="OwnRules"/> is known.
/// </param>
/// <param name="UnknownInputs">
/// The inputs whose state the board can't tell now, though it has them: a Shelly's input whose setting was changed on
/// the device during the connection (made a button, or turned off), until the next connection. Their bit in
/// <see cref="Inputs"/> is 0. The screens show them as unknown, and the app never logs a change of them or acts on one.
/// None on every other board.
/// </param>
public sealed record BoardState(bool IsConnected, ChannelMask Relays, ChannelMask Inputs, DateTimeOffset? LastContactUtc, ConnectionProblem? Problem = null, AnalogReadings? Analog = null, OwnRules? OwnRules = null, PowerOnStates? PowerOn = null, ChannelMask KeptUnavailable = default, ChannelMask UnknownInputs = default)
{
    public static BoardState Initial { get; } = new(false, ChannelMask.None, ChannelMask.None, null);

    /// <summary>
    /// Whether the board has answered at all since the app started. Until it has, <see cref="Relays"/> and
    /// <see cref="Inputs"/> are only "none", not what the board reads, and the screens show them as unknown.
    /// </summary>
    public bool HasAnswered => IsConnected || LastContactUtc is not null;

    /// <summary>
    /// The relays the board controls by itself and ignores the app's commands for (<see cref="Boards.OwnRules.Unavailable"/>);
    /// while its rules aren't known, the ones it last showed (<see cref="KeptUnavailable"/>).
    /// </summary>
    public ChannelMask Unavailable => OwnRules is { } rules ? rules.Unavailable : KeptUnavailable;

    /// <summary>
    /// Whether the board can tell the input's state, 1 being the first: it has answered, and the input isn't one of
    /// <see cref="UnknownInputs"/>.
    /// </summary>
    public bool IsInputKnown(int input)
    {
        return HasAnswered && !UnknownInputs.Contains(input);
    }

    /// <summary>Whether a relay is on, 1 being the first.</summary>
    public bool IsOn(int relay)
    {
        return Relays.Contains(relay);
    }
}
