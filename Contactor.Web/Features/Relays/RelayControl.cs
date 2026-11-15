using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// Turns relays on and off for a person: sends the command to the relay's own board (its <see cref="Relay.BoardId"/>,
/// on its channel) first, and only once it went out records who did it (<see cref="RelayRuntime"/>, auto-off time) and
/// logs the event with that board. If that board is not connected the command fails with
/// <see cref="BoardNotConnectedException"/> and nothing is recorded or queued; another board is never touched. A relay in
/// a group is only turned on and off through its group (<see cref="GroupSequencer"/>), which keeps the group's order and
/// interlock.
/// </summary>
public sealed class RelayControl
{
    private readonly BoardRegistry _registry;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly GroupSequencer _groups;

    public RelayControl(
        BoardRegistry registry,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        GroupSequencer groups)
    {
        _registry = registry;
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _groups = groups;
    }

    /// <summary>
    /// Turns a relay on for a person (their e-mail) or an input (<see cref="EventActors.ForInput"/>), as a press of its
    /// button: with the relay's auto-off.
    /// </summary>
    public async Task TurnOnAsync(int relayId, string actor, CancellationToken cancellationToken)
    {
        var (board, mask) = await TargetAsync(relayId, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureNotInGroupAsync(board, mask, cancellationToken);
        await SendAsync(board, mask, on: true, cancellationToken);

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var maxOnMinutes = (await db.Relays.Where(r => r.Id == relayId).Select(r => new { r.MaxOnMinutes }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new BoardNotConnectedException(BoardRemoved)).MaxOnMinutes;
        await RelayRuns.StartAsync(db, relayId, actor, now, maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null, cancellationToken);
        db.EventLog.Add(new EventLogEntry { TimestampUtc = now, BoardId = board.BoardId, RelayId = relayId, Actor = actor, Action = EventActions.On });
        await SaveAsync(db, board.BoardId, cancellationToken);
        _changes.Notify();
    }

    /// <summary>
    /// Pulses a relay for a person: the board turns it on, and off again by itself after <paramref name="length"/>, also
    /// when the app stops in between. Only on a board with a pulse that can do that length
    /// (<see cref="BoardCapabilities.Pulse"/>), at most <see cref="MaxPulse"/>, and never for a relay in a group, whose
    /// interlock the board's own OFF would ignore. Throws <see cref="NotSupportedException"/> without a pulse, and
    /// <see cref="ArgumentOutOfRangeException"/> for a length it can't do. Logged as one event. The relay's run is the person's, with an auto-off a little after the pulse as
    /// a backstop; the board's own OFF is expected, so it isn't taken for a change outside the app.
    /// </summary>
    public async Task PulseAsync(int relayId, TimeSpan length, string actor, CancellationToken cancellationToken)
    {
        var (board, mask) = await TargetAsync(relayId, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (board.Capabilities.Pulse is not { } pulse)
        {
            throw new NotSupportedException("This board can't pulse a relay.");
        }

        if (length > MaxPulse || !pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The board can't pulse for that long, or it is longer than a try.");
        }

        await EnsureNotInGroupAsync(board, mask, cancellationToken);
        var expected = board.Expected;
        expected.Expect(mask, on: true);

        // The board's own OFF comes after the pulse, and on a board the app polls up to a poll or two later.
        expected.Expect(mask, on: false, length + PulseOffWait);
        try
        {
            await board.PulseAsync(mask, length, cancellationToken);
        }
        catch
        {
            // As for ON: a failed or cancelled pulse logs nothing, and whatever the board did shows as an external change.
            expected.Forget(mask, on: true);
            expected.Forget(mask, on: false);
            throw;
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await RelayRuns.StartAsync(db, relayId, actor, now, now + length + PulseBackstop, cancellationToken);
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = now,
            BoardId = board.BoardId,
            RelayId = relayId,
            Actor = actor,
            Action = EventActions.Pulse,
            Details = JsonSerializer.Serialize(new { milliseconds = (int)length.TotalMilliseconds }),
        });
        await SaveAsync(db, board.BoardId, cancellationToken);
        _changes.Notify();
    }

    /// <summary>How long after a pulse's end its auto-off comes, should the board not have turned the relay off.</summary>
    public static readonly TimeSpan PulseBackstop = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest pulse: only short ones, for "Try", until longer ones are designed. A long one could end by itself
    /// after the relay joined a group, below members still on.
    /// </summary>
    public static readonly TimeSpan MaxPulse = TimeSpan.FromSeconds(30);

    /// <summary>How long after a pulse's end its OFF is still expected: a slow or failed poll or two on a polled board.</summary>
    public static readonly TimeSpan PulseOffWait = TimeSpan.FromSeconds(30);

    /// <summary>Turns a relay off for a person or an input, as a press of its button.</summary>
    public async Task TurnOffAsync(int relayId, string actor, CancellationToken cancellationToken)
    {
        var (board, mask) = await TargetAsync(relayId, cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureNotInGroupAsync(board, mask, cancellationToken);
        var sent = _clock.UtcNow;
        await SendAsync(board, mask, on: false, cancellationToken);

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await RelayRuns.EndAsync(db, relayId, sent, cancellationToken);
        db.EventLog.Add(new EventLogEntry { TimestampUtc = now, BoardId = board.BoardId, RelayId = relayId, Actor = actor, Action = EventActions.Off });
        await SaveAsync(db, board.BoardId, cancellationToken);
        _changes.Notify();
    }

    /// <summary>
    /// "All off" for one board: turns every relay of that board outside a group off with one command and logs one event
    /// for each that was on, then stops every group of that board in its own order, with its run-on times. Throws
    /// <see cref="BoardNotConnectedException"/> when that board isn't connected; nothing is sent or queued then.
    /// </summary>
    public Task TurnAllOffAsync(int boardId, string userEmail, CancellationToken cancellationToken)
    {
        return TurnAllOffAsync(boardId, userEmail, logOff: true, cancellationToken);
    }

    /// <summary>
    /// "All off" for one board once more, as <see cref="TurnAllOffAsync(int, string, CancellationToken)"/> but without
    /// an "Off" event for each relay: for a board being removed, whose first "All off" logged them, while it waits for
    /// the board to show every relay off. A group's stop is logged as ever (one already stopping logs nothing again).
    /// </summary>
    public Task TurnAllOffAgainAsync(int boardId, string userEmail, CancellationToken cancellationToken)
    {
        return TurnAllOffAsync(boardId, userEmail, logOff: false, cancellationToken);
    }

    private async Task TurnAllOffAsync(int boardId, string userEmail, bool logOff, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        var board = _registry.Find(boardId) ?? throw new BoardNotConnectedException($"The app runs no relay board {boardId}.");
        if (!board.State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        var expected = board.Expected;
        var single = board.Capabilities.AllRelays.Except(await _groups.MemberMaskAsync(boardId, cancellationToken));
        var wasOn = board.State.Relays & single;
        var sent = _clock.UtcNow;
        var refused = ChannelMask.None;
        if (!single.IsEmpty)
        {
            expected.Expect(wasOn, on: false);
            try
            {
                // Not taken: refused, or an answer the app couldn't read. The board's state, which already holds what
                // the command read, tells: one it shows off went off all the same.
                var taken = await board.TurnOffAsync(single, cancellationToken);
                refused = single.Except(taken) & board.State.Relays;
            }
            catch
            {
                // A failed or cancelled command logs nothing: whatever the board did shows as an external change.
                expected.Forget(wasOn, on: false);
                throw;
            }

            // A relay the board refused (a board that switches a relay per request can refuse some) didn't go off: it
            // isn't logged, its run stays, and whatever it does later is its own change.
            // Only the expectations this command added: a relay that was off before isn't this command's.
            expected.Forget(wasOn & refused, on: false);
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var channels = await BoardChannels.LoadAsync(db, boardId, cancellationToken);

        // Only this board's relays' runs: another board's relays weren't sent anything.
        var relayIds = channels.AllRelayIds.ToList();
        foreach (var relayId in await db.RelayRuntimes.Where(r => r.OnSinceUtc != null && relayIds.Contains(r.RelayId)).Select(r => r.RelayId).ToListAsync(cancellationToken))
        {
            if (channels.RelayChannel(relayId) is not { } channel || !refused.Contains(channel))
            {
                await RelayRuns.EndAsync(db, relayId, sent, cancellationToken);
            }
        }

        // Logged once: an "All off" again for a board being removed logs nothing more.
        if (logOff)
        {
            foreach (var relayId in channels.RelayIds(wasOn.Except(refused)))
            {
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    BoardId = boardId,
                    RelayId = relayId,
                    Actor = userEmail,
                    Action = EventActions.Off,
                    Details = """{"allOff":true}""",
                });
            }
        }

        await SaveAsync(db, board.BoardId, cancellationToken);
        _changes.Notify();
        await _groups.StopAllAsync(boardId, userEmail, cancellationToken);
    }

    /// <summary>
    /// "All off" on every board at once (<see cref="TurnAllOffAsync"/> for each). A board without contact is skipped:
    /// nothing is sent or queued for it, and it is returned, with every board that was lost on the way, so the person
    /// can be told. Every board is tried, whatever happens to another; a failure other than a lost board is thrown once
    /// all have been tried.
    /// </summary>
    /// <returns>The ids of the boards that couldn't be reached, in the list's order.</returns>
    public async Task<IReadOnlyList<int>> TurnOffAllBoardsAsync(string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        var boards = _registry.All;
        var results = await Task.WhenAll(boards.Select(async board =>
        {
            try
            {
                await TurnAllOffAsync(board.BoardId, userEmail, cancellationToken);
                return (Reached: true, Failure: (Exception?)null);
            }
            catch (BoardNotConnectedException)
            {
                return (Reached: false, Failure: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Reached: true, Failure: ex);
            }
        }));

        if (results.Select(r => r.Failure).OfType<Exception>().FirstOrDefault() is { } failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }

        return [.. boards.Zip(results).Where(b => !b.Second.Reached).Select(b => b.First.BoardId)];
    }

    // Expects the change on the board, then sends it. A command that fails or is cancelled logs nothing, so its
    // expectation goes too: whatever the board did then shows as an external change rather than vanishing, with an
    // auto-off of its own.
    private static async Task SendAsync(LiveBoard board, ChannelMask mask, bool on, CancellationToken cancellationToken)
    {
        board.Expected.Expect(mask, on);
        try
        {
            if (on)
            {
                await board.TurnOnAsync(mask, cancellationToken);
            }
            else
            {
                await board.TurnOffAsync(mask, cancellationToken);
            }
        }
        catch
        {
            board.Expected.Forget(mask, on);
            throw;
        }
    }

    private const string BoardRemoved = "The relay board was removed.";

    // What a command saves once it went out. The board may have been removed meanwhile, its rows with it: that fails as
    // a board the app doesn't run, not as a broken database.
    private async Task SaveAsync(ContactorDbContext db, int boardId, CancellationToken cancellationToken)
    {
        try
        {
            await RelayRuns.SaveAsync(db, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException && _registry.Find(boardId) is null)
        {
            throw new BoardNotConnectedException(BoardRemoved, ex);
        }
    }

    // The relay's own board and its channel there, as a mask: from the relay's row. Throws for a relay that doesn't
    // exist, or one its board doesn't have (above its count), before anything is sent; and
    // BoardNotConnectedException for a relay whose board the app doesn't run.
    private async Task<(LiveBoard Board, ChannelMask Mask)> TargetAsync(int relayId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var relay = await db.Relays.AsNoTracking()
            .Where(r => r.Id == relayId)
            .Select(r => new { r.BoardId, r.Channel })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentOutOfRangeException(nameof(relayId), relayId, "There is no such relay.");
        var board = _registry.Find(relay.BoardId)
            ?? throw new BoardNotConnectedException($"The app runs no relay board {relay.BoardId}.");
        return (board, board.Capabilities.Relay(relay.Channel));
    }

    private async Task EnsureNotInGroupAsync(LiveBoard board, ChannelMask mask, CancellationToken cancellationToken)
    {
        if ((await _groups.MemberMaskAsync(board.BoardId, cancellationToken)).Overlaps(mask))
        {
            throw new RelayInGroupException();
        }
    }
}

/// <summary>
/// Tells open pages that relay data or the event log changed (someone turned a relay on or off, something happened
/// at the board), so every phone shows it right away. The board's own status comes from
/// <see cref="IRelayBoard.StateChanged"/>.
/// </summary>
public sealed class RelayChanges
{
    public event EventHandler? Changed;

    public void Notify()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A relay in a group was to be switched on its own; only its group may do that.</summary>
public sealed class RelayInGroupException : InvalidOperationException
{
    /// <summary>What a page shows.</summary>
    public const string Text = "This relay is in a group and is switched from the group.";

    public RelayInGroupException()
        : base("The relay is in a group; turn it on and off through the group.")
    {
    }

    public RelayInGroupException(string message)
        : base(message)
    {
    }

    public RelayInGroupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
