using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// "3 of 8 on" for each board, as the Relays screen counts: its active relays in no group, and each of its groups as
/// one; on is what the board has answered. The board filter shows it on Relays, Events and Debug alike.
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

    /// <summary>What there is to count now, from the database, for a screen that doesn't list the relays itself.</summary>
    public static async Task<BoardCounts> LoadAsync(ContactorDbContext db, BoardRegistry registry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(registry);
        var members = await db.RelayGroupMembers.AsNoTracking()
            .Select(m => new { m.GroupId, m.RelayId, m.Relay!.Channel })
            .ToListAsync(cancellationToken);
        var inGroups = members.Select(m => m.RelayId).ToHashSet();
        var relays = await db.Relays.AsNoTracking()
            .Where(r => r.IsActive)
            .Select(r => new { r.Id, r.BoardId, r.Channel })
            .ToListAsync(cancellationToken);
        var groups = await db.RelayGroups.AsNoTracking().Select(g => new { g.Id, g.BoardId }).ToListAsync(cancellationToken);
        return new BoardCounts(
            relays.Where(r => !inGroups.Contains(r.Id) && registry.HasRelay(r.BoardId, r.Channel)).Select(r => (r.BoardId, r.Channel)),
            groups.Select(g => (g.BoardId, ChannelMask.Of(members.Where(m => m.GroupId == g.Id).Select(m => m.Channel)))));
    }
}
