namespace Contactor.Boards;

/// <summary>
/// A relay board as the app sees it, whatever its make: its state, and commands to switch its relays. Commands are
/// sent at once or not at all: without a connection they throw <see cref="BoardNotConnectedException"/> and are never
/// queued for later. A command doesn't wait for the board to confirm it; the confirmation is the next
/// <see cref="StateChanged"/>, which may not come when nothing changed.
/// </summary>
public interface IRelayBoard
{
    BoardCapabilities Capabilities { get; }

    /// <summary>The latest state the board reported.</summary>
    BoardState State { get; }

    /// <summary>
    /// Raised on every new state, when the connection is lost or comes back, and when the state is forgotten for a new
    /// address (<see cref="IBoardConnection.SetEndpoint"/>). From any thread; each subscriber is
    /// called on its own, so one that throws doesn't keep the state from the others.
    /// </summary>
    event EventHandler<BoardState>? StateChanged;

    /// <summary>
    /// Raised when only the analog readings changed (<see cref="BoardState.Analog"/>): not the relays, the inputs or the
    /// connection, so the safety services, which listen to <see cref="StateChanged"/>, are never woken by a value that
    /// moves a little on every reading. A change that comes with one of those comes as <see cref="StateChanged"/>
    /// instead, whose state carries the readings too: whoever shows the readings listens to both. From any thread, each
    /// subscriber on its own. A board without analog inputs never raises it.
    /// </summary>
    event EventHandler<BoardState>? AnalogChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when only the board's own rules changed (<see cref="BoardState.OwnRules"/>): not the relays, the inputs or
    /// the connection. A change that comes with one of those comes as <see cref="StateChanged"/> instead, whose state
    /// carries the rules too: whoever shows the rules listens to both. From any thread, each subscriber on its own. A
    /// board that doesn't read its rules (<see cref="OwnRulesAbility.Reads"/>) never raises it.
    /// </summary>
    event EventHandler<BoardState>? OwnRulesChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Raised when only the relays' states after a power cut changed (<see cref="BoardState.PowerOn"/>): not the relays,
    /// the inputs or the connection. A change that comes with one of those comes as <see cref="StateChanged"/> instead,
    /// whose state carries them too: whoever shows them listens to both. From any thread, each subscriber on its own. A
    /// board without the ability (<see cref="BoardCapabilities.PowerOn"/>) never raises it.
    /// </summary>
    event EventHandler<BoardState>? PowerOnChanged
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Turns on the relays in <paramref name="relays"/>; the others are left as they are. Returns the relays the board
    /// took: all of them on a board that takes a mask at once, and on one that switches a relay per request, those
    /// it confirmed (not refused, and with an answer the app could read; one it couldn't may have switched, which the
    /// next state shows). When it took none, it throws instead.
    /// </summary>
    Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default);

    /// <summary>Turns off the relays in <paramref name="relays"/>, as <see cref="TurnOnAsync"/> turns them on.</summary>
    Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pulses the relays in <paramref name="relays"/>: turns them on, and the board turns them off again by itself after
    /// <paramref name="length"/>, also when the app stops in between. Returns the relays the board took, as
    /// <see cref="TurnOnAsync"/>. Throws <see cref="NotSupportedException"/> on a board without a pulse
    /// (<see cref="BoardCapabilities.Pulse"/> null), and <see cref="ArgumentOutOfRangeException"/> for a length it can't
    /// do (<see cref="PulseAbility.CanDo"/>), before anything is sent.
    /// </summary>
    Task<ChannelMask> PulseAsync(ChannelMask relays, TimeSpan length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns off the board's own <paramref name="rules"/>, and only those, as a person asked (never on the app's own
    /// accord). Sent at once or not at all, as <see cref="TurnOnAsync"/>: without a connection it throws
    /// <see cref="BoardNotConnectedException"/> and is never queued. Throws <see cref="ArgumentOutOfRangeException"/> for
    /// a relay or a kind of rule the board doesn't have, before anything is sent. A rule given more than once is taken
    /// once. Returns the rules the board took, each once, and none (sending nothing) for none asked; when it took none
    /// of them, it throws instead. Turning off a rule turns off the units behind it (<see cref="OwnRules.Units"/>), so it
    /// may also remove other rules (<see cref="OwnRules.GoesWith(IEnumerable{OwnRule})"/>), e.g. a schedule that switches
    /// two relays: those aren't returned. A rule the app can't turn off (<see cref="OwnRules.CantTurnOff"/>, as the
    /// board last reported) isn't taken and nothing is sent for it. The next state confirms what is gone
    /// (<see cref="BoardState.OwnRules"/>).
    /// Throws <see cref="NotSupportedException"/> on a board that can't turn its rules off
    /// (<see cref="OwnRulesAbility.TurnsOff"/>), the default.
    /// </summary>
    Task<IReadOnlyList<OwnRule>> TurnOffOwnRulesAsync(IReadOnlyCollection<OwnRule> rules, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This board can't turn its own rules off.");
    }

    /// <summary>
    /// Sets what <paramref name="relay"/> does when the board gets power back (<see cref="PowerOnState"/>), as a person
    /// saved it (never on the app's own accord). On a board whose state is for the whole board
    /// (<see cref="PowerOnAbility.PerRelay"/> false), it sets it for every relay. Sent at once or not at all, as
    /// <see cref="TurnOnAsync"/>: without a connection it throws <see cref="BoardNotConnectedException"/> and is never
    /// queued; when the board doesn't take it, it throws <see cref="BoardNotConnectedException"/> too. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a relay or a state the board doesn't have
    /// (<see cref="PowerOnAbility.Has"/>), before anything is sent. The board reads it back, and the next state confirms
    /// it (<see cref="BoardState.PowerOn"/>). Throws <see cref="NotSupportedException"/> on a board without the ability
    /// (<see cref="BoardCapabilities.PowerOn"/> null), the default.
    /// </summary>
    Task SetPowerOnAsync(int relay, PowerOnState state, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("This board can't set its relays' state after a power cut.");
    }

    /// <summary>Asks the board for its state; the answer comes as <see cref="StateChanged"/>.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
