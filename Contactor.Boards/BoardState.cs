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
public sealed record BoardState(bool IsConnected, ChannelMask Relays, ChannelMask Inputs, DateTimeOffset? LastContactUtc, ConnectionProblem? Problem = null, AnalogReadings? Analog = null, OwnRules? OwnRules = null, PowerOnStates? PowerOn = null)
{
    public static BoardState Initial { get; } = new(false, ChannelMask.None, ChannelMask.None, null);

    /// <summary>
    /// Whether the board has answered at all since the app started. Until it has, <see cref="Relays"/> and
    /// <see cref="Inputs"/> are only "none", not what the board reads, and the screens show them as unknown.
    /// </summary>
    public bool HasAnswered => IsConnected || LastContactUtc is not null;

    /// <summary>
    /// The relays the board controls by itself and ignores the app's commands for (<see cref="Boards.OwnRules.Unavailable"/>);
    /// none while its rules aren't known.
    /// </summary>
    public ChannelMask Unavailable => OwnRules?.Unavailable ?? ChannelMask.None;

    /// <summary>Whether a relay is on, 1 being the first.</summary>
    public bool IsOn(int relay)
    {
        return Relays.Contains(relay);
    }
}
