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
        var lamps = Lamps(boardId, state);
        return (lamps.Count(on => on), lamps.Count);
    }

    /// <summary>
    /// One lamp for each relay and group that <see cref="Of"/> counts, true when on, in the order of their channels (a
    /// group in the place of its first relay), so the lamps and "3 of 8 on" always agree.
    /// </summary>
    public IReadOnlyList<bool> Lamps(int boardId, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return
        [
            .. _relays.Where(r => r.BoardId == boardId).Select(r => (First: r.Channel, On: state.IsOn(r.Channel)))
                .Concat(_groups.Where(g => g.BoardId == boardId).Select(g => (First: First(g.Mask), On: state.Relays.Overlaps(g.Mask))))
                .OrderBy(l => l.First)
                .Select(l => l.On),
        ];
    }

    // A group's first channel; a group with no relays goes last.
    private static int First(ChannelMask mask)
    {
        return mask.IsEmpty ? int.MaxValue : mask.Channels.Min();
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
