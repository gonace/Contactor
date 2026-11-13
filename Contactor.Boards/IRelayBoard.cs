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
    /// Turns on the relays in <paramref name="relays"/>; the others are left as they are. Returns the relays the board
    /// took: all of them on a board that takes a mask at once, and on one that switches a relay per request, those
    /// it confirmed (not refused, and with an answer the app could read; one it couldn't may have switched, which the
    /// next state shows). When it took none, it throws instead.
    /// </summary>
    Task<ChannelMask> TurnOnAsync(ChannelMask relays, CancellationToken cancellationToken = default);

    /// <summary>Turns off the relays in <paramref name="relays"/>, as <see cref="TurnOnAsync"/> turns them on.</summary>
    Task<ChannelMask> TurnOffAsync(ChannelMask relays, CancellationToken cancellationToken = default);

    /// <summary>Asks the board for its state; the answer comes as <see cref="StateChanged"/>.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
