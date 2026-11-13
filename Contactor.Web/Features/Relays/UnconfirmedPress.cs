using Contactor.Boards;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// A press of a relay's button that the board didn't confirm: what the person asked for, shown with "Try again" and
/// "Leave it" until they choose or the board shows the relay as they wanted it. Nothing is guessed or queued.
/// </summary>
public sealed record UnconfirmedPress(int RelayId, bool On)
{
    /// <summary>Whether the board now shows the relay as the person wanted it, so there is nothing left to decide.</summary>
    public bool IsSettledBy(BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.IsConnected && state.IsOn(RelayId) == On;
    }
}
