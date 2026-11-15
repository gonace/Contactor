using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// Turns relays off when their auto-off time has passed: every 15 s it looks for a <see cref="RelayRuntime"/> whose
/// <see cref="RelayRuntime.AutoOffAtUtc"/> is due, sends OFF if the board says the relay is still on, logs it as
/// Actor <c>auto-off</c>, and clears the runtime. A person's or an input's ON sets an auto-off time, and so does a relay
/// on with nobody's run (given one here, at every check); a relay a schedule turned on is never touched. A relay the
/// board shows off loses a run left on it (ended here, at every check). Each board is checked on its own, with its own
/// state, expected changes and relays, and the OFF goes to the relay's own board: while a board is disconnected nothing
/// happens to its relays, and a time that passed meanwhile (also before a restart) is handled at the first check after
/// its connection is back; another board's relays are checked as usual. Each board has its own loop
/// (<see cref="IBoardWatcher"/>), so a board that is slow or failing never holds up another's checks.
/// </summary>
public sealed partial class AutoOffService : BackgroundService, IBoardWatcher
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AutoOffService> _logger;
    private readonly BoardParts<BoardAutoOff> _boards;

    public AutoOffService(
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILogger<AutoOffService> logger)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;
        _boards = new BoardParts<BoardAutoOff>(logger);
    }

    /// <summary>How many boards' loops run now (see <see cref="BoardParts{T}.RunningLoops"/>).</summary>
    public int RunningLoops => _boards.RunningLoops;

    /// <summary>
    /// On every board, each on its own: gives every relay that is on with nobody's run its own auto-off, handles every
    /// auto-off that is due now, and ends the run of every relay the board shows off. A board that is disconnected, slow
    /// or failing doesn't hold up another's. Returns the relays it turned off; throws the first failure once every
    /// board has been checked.
    /// </summary>
    public async Task<IReadOnlyList<int>> CheckAsync(CancellationToken cancellationToken)
    {
        var turnedOff = await Task.WhenAll(_boards.All.Select(b => CheckAsync(b.Board, cancellationToken)));
        return [.. turnedOff.SelectMany(t => t)];
    }

    /// <summary>As <see cref="CheckAsync(CancellationToken)"/>, for one board's relays only.</summary>
    public async Task<IReadOnlyList<int>> CheckAsync(LiveBoard board, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(board);
        var state = board.State;
        if (!state.IsConnected)
        {
            return [];
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var channels = await BoardChannels.LoadAsync(db, board.BoardId, cancellationToken);
        if (await GiveOwnerlessRelaysAutoOffAsync(db, board, channels, state, now, cancellationToken))
        {
            _changes.Notify();
        }

        var relayIds = channels.AllRelayIds.ToList();
        var due = await db.RelayRuntimes
            .Where(r => r.AutoOffAtUtc != null && r.AutoOffAtUtc <= now && relayIds.Contains(r.RelayId))
            .OrderBy(r => r.RelayId)
            .ToListAsync(cancellationToken);
        var expected = board.Expected;

        List<int> turnedOff = [];
        foreach (var runtime in due)
        {
            // The board's state and the command go by the relay's channel; a relay the board doesn't have isn't on.
            if (OnBoard(board, channels, runtime.RelayId) is { } channel && state.IsOn(channel))
            {
                var sent = _clock.UtcNow;
                var mask = board.Capabilities.Relay(channel);
                expected.Expect(mask, on: false);
                try
                {
                    await board.TurnOffAsync(mask, cancellationToken);
                }
                catch (BoardNotConnectedException)
                {
                    // Lost the board mid-way: the rest waits for the next check. Nothing is logged, so whatever the
                    // board did shows as an external change.
                    expected.Forget(mask, on: false);
                    break;
                }

                var minutes = runtime.OnSinceUtc is { } since ? (int)Math.Round((runtime.AutoOffAtUtc!.Value - since).TotalMinutes) : (int?)null;
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    BoardId = board.BoardId,
                    RelayId = runtime.RelayId,
                    Actor = EventActors.AutoOff,
                    Action = EventActions.Off,
                    Details = minutes is null ? null : JsonSerializer.Serialize(new { afterMinutes = minutes }),
                });
                turnedOff.Add(runtime.RelayId);

                // Its run ends; one begun after the OFF went out (someone turned it on again meanwhile) stays.
                await RelayRuns.EndAsync(db, runtime.RelayId, sent, cancellationToken);
                continue;
            }

            // Off already, turned off some other way: nothing to send, but the plan is stale. Only the run as read
            // ends; one saved meanwhile (someone turned it on again) stays.
            await RelayRuns.EndOutsideAsync(db, runtime.RelayId, cancellationToken);
        }

        // Not after the board was lost on the way: nothing happens without it.
        if (board.State.IsConnected)
        {
            await EndRunsOfOffRelaysAsync(db, board, channels, state, [.. due.Select(r => r.RelayId)], cancellationToken);
        }

        if (db.ChangeTracker.HasChanges())
        {
            await RelayRuns.SaveAsync(db, cancellationToken);
            _changes.Notify();
        }

        return turnedOff;
    }

    // A relay the board shows off keeps no run: one can be left when the change seen outside the app gave way to another
    // writer's save, or when the relay went off while the app was down (at start the recorder compares no relays).
    // Its status line would keep saying "On since". Not while an ON is still on its way (a person's or a schedule's run
    // is saved before the board shows the relay on), and only the run as read ends: one saved meanwhile stays. Nobody
    // knows who turned it off or when, so nothing is logged.
    private async Task EndRunsOfOffRelaysAsync(ContactorDbContext db, LiveBoard board, BoardChannels channels, BoardState state, HashSet<int> handled, CancellationToken cancellationToken)
    {
        var expected = board.Expected;
        var off = Enumerable.Range(1, board.Capabilities.RelayCount)
            .Where(c => !state.IsOn(c) && !expected.IsPending(c, on: true))
            .Select(channels.RelayAt)
            .OfType<int>()
            .Where(r => !handled.Contains(r))
            .ToList();
        if (off.Count == 0)
        {
            return;
        }

        // Tracked, so the end is checked against these rows' versions. An ON whose run was saved just before they were
        // read is either still on its way, or the board shows it on by now: the state at the start of the check may be
        // older. Pending first: the board's state changes before the expectation is used up.
        var stale = await db.RelayRuntimes
            .Where(r => off.Contains(r.RelayId) && r.OnSinceUtc != null)
            .ToListAsync(cancellationToken);
        foreach (var run in stale.Where(r => channels.RelayChannel(r.RelayId) is { } c && !expected.IsPending(c, on: true) && !board.State.IsOn(c)))
        {
            // A pulse's run ends here as a rule: the board turned the relay off by itself, as it should.
            var pulse = IsPulse(run);
            await RelayRuns.EndOutsideAsync(db, run.RelayId, cancellationToken);
            if (pulse)
            {
                LogPulseOver(run.RelayId);
            }
            else
            {
                LogStaleRun(run.RelayId);
            }
        }
    }

    // A pulse's run has its auto-off just after the pulse (RelayControl.PulseAsync), far sooner than any relay's limit.
    private static bool IsPulse(RelayRuntime run)
    {
        return run.OnSinceUtc is { } since
            && run.AutoOffAtUtc is { } autoOff
            && autoOff - since <= RelayControl.MaxPulse + RelayControl.PulseBackstop;
    }

    // A relay can be on with nobody's run without the app seeing it come on, e.g. after an OFF the board was sent but
    // didn't carry out. It gets the relay's own auto-off from now, as one that came on outside the app, so nothing runs
    // for ever by accident. A relay in a group is left to its group (GroupPlanner.GiveOwnerlessAutoOff). Each update
    // only applies while the relay still has no run, so a person's or a schedule's ON saved meanwhile keeps theirs.
    private async Task<bool> GiveOwnerlessRelaysAutoOffAsync(ContactorDbContext db, LiveBoard board, BoardChannels channels, BoardState state, DateTime now, CancellationToken cancellationToken)
    {
        var on = channels.RelayIds(state.Relays & board.Capabilities.AllRelays).ToList();
        if (on.Count == 0)
        {
            return false;
        }

        var members = db.RelayGroupMembers.Select(m => m.RelayId);
        var ownerless = await db.Relays.AsNoTracking()
            .Where(r => on.Contains(r.Id) && !members.Contains(r.Id))
            .Where(r => !db.RelayRuntimes.Any(x => x.RelayId == r.Id && x.OnSinceUtc != null))
            .Select(r => new { r.Id, r.Channel, r.MaxOnMinutes })
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (var relay in ownerless)
        {
            // Turned off meanwhile, e.g. by a person: nothing to give a run to.
            if (!board.State.IsOn(relay.Channel))
            {
                continue;
            }

            var autoOffAt = relay.MaxOnMinutes is { } minutes ? now.AddMinutes(minutes) : (DateTime?)null;
            if (await RelayRuns.GiveOwnAutoOffNowAsync(db, relay.Id, now, autoOffAt, cancellationToken))
            {
                LogOwnerless(relay.Id);
                changed = true;
            }
        }

        return changed;
    }

    // The relay's channel, or null for a relay the board doesn't have (left from a board with more relays).
    private static int? OnBoard(LiveBoard board, BoardChannels channels, int relayId)
    {
        return channels.RelayChannel(relayId) is { } channel && channel <= board.Capabilities.RelayCount ? channel : null;
    }

    /// <inheritdoc/>
    public IAsyncDisposable Watch(LiveBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var part = new BoardAutoOff(this, board);
        _boards.Add(part);
        return part;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return _boards.RunAsync(stoppingToken);
    }

    // One board's loop: a check every 15 s, so a board that is slow or failing holds up only its own. Each check runs
    // under the part's gate, so the part's end waits for it.
    private async Task RunAsync(BoardAutoOff part, CancellationToken stoppingToken)
    {
        var board = part.Board;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await part.Gate.WaitAsync(stoppingToken);
                try
                {
                    if (part.Ended)
                    {
                        return;
                    }

                    await CheckAsync(board, stoppingToken);
                }
                finally
                {
                    part.Gate.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One failed check must not stop auto-off for good; it is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCheckFailed(ex, board.BoardId);
            }

            try
            {
                await Task.Delay(CheckInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // A board's part of auto-off: its loop, ended with its board's watch. It listens to nothing: it reads the board's
    // state at each check. A check under way finishes before the watch ends, so nothing is sent to a board being
    // disposed.
    private sealed class BoardAutoOff : IBoardPart, IAsyncDisposable
    {
        private readonly AutoOffService _service;
        private readonly LiveBoard _board;
        private readonly CancellationTokenSource _ended = new();
        private int _disposed;

        public BoardAutoOff(AutoOffService service, LiveBoard board)
        {
            _service = service;
            _board = board;
        }

        public int BoardId => _board.BoardId;

        public LiveBoard Board => _board;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool Ended => Volatile.Read(ref _disposed) == 1;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ended.Token);
            await _service.RunAsync(this, linked.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _service._boards.Remove(this);
            await Gate.WaitAsync(CancellationToken.None);
            Gate.Release();
            await _ended.CancelAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Auto-off check of relay board {BoardId} failed")]
    private partial void LogCheckFailed(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay {RelayId} is on with nobody's run: it gets its own auto-off")]
    private partial void LogOwnerless(int relayId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay {RelayId} is off but has a run: ending it")]
    private partial void LogStaleRun(int relayId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Relay {RelayId}'s pulse is over: its run ends")]
    private partial void LogPulseOver(int relayId);
}
