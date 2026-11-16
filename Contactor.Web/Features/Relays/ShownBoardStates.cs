using Contactor.Boards;
using Contactor.Web.Features.Boards;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// What the board filter (<c>BoardPicker</c>) shows of each board's state: its contact, whether it has answered, its
/// relays, and why it refuses the connection. A page redraws for a board's new status only when it changes one of these, so a status that changes
/// nothing (a poll's answer) doesn't redraw it. Safe from any thread: the statuses come from the boards' own loops.
/// </summary>
public sealed class ShownBoardStates
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, (bool Connected, bool Answered, ChannelMask Relays, ConnectionProblemKind? Problem)> _shown = [];

    /// <summary>Starts again from these boards as they are now; a board that is gone is forgotten.</summary>
    public void Reset(IEnumerable<LiveBoard> boards)
    {
        ArgumentNullException.ThrowIfNull(boards);
        lock (_lock)
        {
            _shown.Clear();
            foreach (var board in boards)
            {
                _shown[board.BoardId] = Of(board.State);
            }
        }
    }

    /// <summary>
    /// Whether this status changes what the filter shows of its board; it is then kept as the board's state shown. A
    /// board not seen before always does.
    /// </summary>
    public bool Changes(BoardStateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var shown = Of(change.State);
        lock (_lock)
        {
            if (_shown.TryGetValue(change.BoardId, out var before) && before == shown)
            {
                return false;
            }

            _shown[change.BoardId] = shown;
            return true;
        }
    }

    private static (bool Connected, bool Answered, ChannelMask Relays, ConnectionProblemKind? Problem) Of(BoardState state)
    {
        return (state.IsConnected, state.HasAnswered, state.Relays, state.Problem?.Kind);
    }
}
