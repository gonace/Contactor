using Contactor.Boards;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Boards;

/// <summary>
/// Which channel on one board each of its relays and inputs is on, read from the database. A relay's and an input's
/// <c>Id</c> is only the database's key: whatever talks to the board, or reads the board's state, goes by the channel,
/// and the screens show the channel ("RELAY 3"). Read fresh where it is needed, so it never goes stale: the rows only
/// change when a board's rows are seeded.
/// </summary>
public sealed class BoardChannels
{
    private readonly Dictionary<int, int> _relayChannels;
    private readonly Dictionary<int, int> _relaysAt;
    private readonly Dictionary<int, int> _inputChannels;
    private readonly Dictionary<int, int> _inputsAt;

    private BoardChannels(int boardId, IReadOnlyCollection<(int Id, int Channel)> relays, IReadOnlyCollection<(int Id, int Channel)> inputs)
    {
        BoardId = boardId;
        _relayChannels = relays.ToDictionary(r => r.Id, r => r.Channel);
        _relaysAt = relays.ToDictionary(r => r.Channel, r => r.Id);
        _inputChannels = inputs.ToDictionary(i => i.Id, i => i.Channel);
        _inputsAt = inputs.ToDictionary(i => i.Channel, i => i.Id);
    }

    /// <summary>The relays and inputs of the board on this row (<see cref="Board.Id"/>).</summary>
    public static async Task<BoardChannels> LoadAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var relays = await db.Relays.AsNoTracking()
            .Where(r => r.BoardId == boardId)
            .Select(r => new { r.Id, r.Channel })
            .ToListAsync(cancellationToken);
        var inputs = await db.Inputs.AsNoTracking()
            .Where(i => i.BoardId == boardId)
            .Select(i => new { i.Id, i.Channel })
            .ToListAsync(cancellationToken);
        return new BoardChannels(boardId, [.. relays.Select(r => (r.Id, r.Channel))], [.. inputs.Select(i => (i.Id, i.Channel))]);
    }

    /// <summary>As <see cref="LoadAsync(ContactorDbContext, int, CancellationToken)"/>, on a context of its own.</summary>
    public static async Task<BoardChannels> LoadAsync(IDbContextFactory<ContactorDbContext> dbFactory, int boardId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await LoadAsync(db, boardId, cancellationToken);
    }

    /// <summary>The board's row.</summary>
    public int BoardId { get; }

    /// <summary>The ids of every relay of the board, also those above its count.</summary>
    public IReadOnlyCollection<int> AllRelayIds => _relayChannels.Keys;

    /// <summary>The relay's channel, or null for a relay that isn't on this board (also one on another board).</summary>
    public int? RelayChannel(int relayId)
    {
        return _relayChannels.TryGetValue(relayId, out var channel) ? channel : null;
    }

    /// <summary>The id of the relay on this channel, or null when there is none.</summary>
    public int? RelayAt(int channel)
    {
        return _relaysAt.TryGetValue(channel, out var relayId) ? relayId : null;
    }

    /// <summary>The input's channel, or null for an input that isn't on the board.</summary>
    public int? InputChannel(int inputId)
    {
        return _inputChannels.TryGetValue(inputId, out var channel) ? channel : null;
    }

    /// <summary>The id of the input on this channel, or null when there is none.</summary>
    public int? InputAt(int channel)
    {
        return _inputsAt.TryGetValue(channel, out var inputId) ? inputId : null;
    }

    /// <summary>The ids of the relays on the channels in <paramref name="mask"/>, in channel order; a channel without a relay is left out.</summary>
    public IEnumerable<int> RelayIds(ChannelMask mask)
    {
        foreach (var channel in mask.Channels)
        {
            if (RelayAt(channel) is { } relayId)
            {
                yield return relayId;
            }
        }
    }
}
