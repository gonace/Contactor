using Contactor.Boards;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// "3 of 8 on" for each board, as the Relays screen counts: its active relays in no group, and each of its groups as
/// one; on is what the board has answered. The Relays screen shows it in its heading and each board's section. The board
/// filter counts every relay of each board instead, as its lamps show them.
/// </summary>
public sealed class BoardCounts
{
    private readonly IReadOnlyList<(int BoardId, int Channel)> _relays;
    private readonly IReadOnlyList<(int BoardId, ChannelMask Mask)> _groups;

    /// <param name="relays">The active relays in no group, on a channel their board has.</param>
    /// <param name="groups">Each group's board and relays.</param>
    public BoardCounts(IEnumerable<(int BoardId, int Channel)> relays, IEnumerable<(int BoardId, ChannelMask Mask)> groups)
    {
        ArgumentNullException.ThrowIfNull(relays);
        ArgumentNullException.ThrowIfNull(groups);
        _relays = [.. relays];
        _groups = [.. groups];
    }

    /// <summary>Nothing to count yet.</summary>
    public static BoardCounts None { get; } = new([], []);

    /// <summary>The board's relays and groups that are on, of those it has, with the board in this state.</summary>
    public (int On, int Active) Of(int boardId, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var active = _relays.Count(r => r.BoardId == boardId) + _groups.Count(g => g.BoardId == boardId);
        var on = _relays.Count(r => r.BoardId == boardId && state.IsOn(r.Channel))
            + _groups.Count(g => g.BoardId == boardId && state.Relays.Overlaps(g.Mask));
        return (on, active);
    }
}
