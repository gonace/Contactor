using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Groups;

/// <summary>
/// Runs the relay groups of every board, each board on its own (<see cref="BoardSequencer"/>): its own lock, its own
/// interlock check first on each of its statuses, its own run-on times and reconnect. Watches every live board from the
/// moment it is made (<see cref="IBoardWatcher"/>), runs each board's loop while the app runs, and passes a request
/// for a group (start, stop, one relay) to the group's own board. A group's relays are all on its board.
/// </summary>
public sealed class GroupSequencer : BackgroundService, IBoardWatcher
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BoardParts<BoardSequencer> _boards;

    public GroupSequencer(
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _boards = new BoardParts<BoardSequencer>(loggerFactory.CreateLogger<GroupSequencer>());
    }

    /// <summary>Every board's sequencer.</summary>
    public IReadOnlyList<BoardSequencer> Boards => _boards.All;

    /// <summary>How many boards' loops run now (see <see cref="BoardParts{T}.RunningLoops"/>).</summary>
    public int RunningLoops => _boards.RunningLoops;

    /// <summary>The board's sequencer, or null for a board the app doesn't run.</summary>
    public BoardSequencer? For(int boardId)
    {
        return _boards.Find(boardId);
    }

    /// <inheritdoc/>
    public IAsyncDisposable Watch(LiveBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var sequencer = new BoardSequencer(board, _dbFactory, _clock, _changes, _timeProvider, _loggerFactory.CreateLogger<BoardSequencer>());
        _boards.Add(sequencer);
        return new Watching(_boards, sequencer);
    }

    /// <summary>
    /// Plans and runs the next step of every board's groups, each board on its own: one that is lost or fails doesn't
    /// keep the others' from running. Throws the first failure once all have run.
    /// </summary>
    public async Task StepAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_boards.All.Select(b => b.StepAsync(cancellationToken)));
    }

    /// <summary>
    /// Starts a group on its board, or carries on from where it is. Throws <see cref="BoardNotConnectedException"/>
    /// without its board.
    /// </summary>
    public async Task StartGroupAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        await (await OfGroupAsync(groupId, cancellationToken)).StartGroupAsync(groupId, actor, cancellationToken);
    }

    /// <summary>Stops a group with its run-on times. Throws <see cref="BoardNotConnectedException"/> without its board.</summary>
    public async Task StopGroupAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        await (await OfGroupAsync(groupId, cancellationToken)).StopGroupAsync(groupId, actor, cancellationToken);
    }

    /// <summary>
    /// Turns one relay of a group on or off, within the interlock. Returns why it can't, or null when done. Throws
    /// <see cref="BoardNotConnectedException"/> without its board.
    /// </summary>
    public async Task<string?> SetMemberAsync(int groupId, int relayId, bool on, string actor, CancellationToken cancellationToken)
    {
        return await (await OfGroupAsync(groupId, cancellationToken)).SetMemberAsync(groupId, relayId, on, actor, cancellationToken);
    }

    /// <summary>Stops every group of the board, with run-on times ("All off" for that board).</summary>
    public async Task StopAllAsync(int boardId, string actor, CancellationToken cancellationToken)
    {
        await Of(boardId).StopAllAsync(actor, cancellationToken);
    }

    /// <summary>The channels of the board's relays that are in a group; none for a board the app doesn't run.</summary>
    public async Task<ChannelMask> MemberMaskAsync(int boardId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return ChannelMask.Of(await db.RelayGroupMembers
            .Where(m => m.Relay!.BoardId == boardId)
            .Select(m => m.Relay!.Channel)
            .ToListAsync(cancellationToken));
    }

    /// <summary>
    /// Makes a group that is on outside the app the actor's (see <see cref="BoardSequencer.TakeOverOwnerlessAsync"/>),
    /// under its board's lock. Throws <see cref="BoardNotConnectedException"/> for a group whose board the app doesn't run.
    /// </summary>
    public async Task<bool> TakeOverOwnerlessAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        return await (await OfGroupAsync(groupId, cancellationToken)).TakeOverOwnerlessAsync(groupId, actor, cancellationToken);
    }

    /// <summary>
    /// Runs a change to the board's groups themselves (create, save, delete) under the same lock as that board's
    /// sequence, so a check like "the group is off" still holds when the change is saved. For a board the app doesn't
    /// run, nothing runs its groups: the change runs at once (and finds no contact with the board where it asks).
    /// </summary>
    public Task<T> ExclusiveAsync<T>(int boardId, Func<Task<T>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return _boards.Find(boardId) is { } sequencer ? sequencer.ExclusiveAsync(change, cancellationToken) : change();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _boards.RunAsync(stoppingToken);
    }

    private BoardSequencer Of(int boardId)
    {
        return _boards.Find(boardId) ?? throw new BoardNotConnectedException($"The app runs no relay board {boardId}.");
    }

    // A group that doesn't exist is left to the board's sequencer to refuse: the app's first board, if any.
    private async Task<BoardSequencer> OfGroupAsync(int groupId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var boardId = await db.RelayGroups.Where(g => g.Id == groupId).Select(g => (int?)g.BoardId).SingleOrDefaultAsync(cancellationToken);
        if (boardId is { } id)
        {
            return Of(id);
        }

        return _boards.All is [var first, ..] ? first : throw new BoardNotConnectedException("The app runs no relay board.");
    }

    // Ends one board's watch: its sequencer leaves the list, then stops.
    private sealed class Watching : IAsyncDisposable
    {
        private readonly BoardParts<BoardSequencer> _boards;
        private readonly BoardSequencer _sequencer;

        public Watching(BoardParts<BoardSequencer> boards, BoardSequencer sequencer)
        {
            _boards = boards;
            _sequencer = sequencer;
        }

        public ValueTask DisposeAsync()
        {
            _boards.Remove(_sequencer);
            return _sequencer.DisposeAsync();
        }
    }
}
