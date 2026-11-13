using System.Text.Json;
using System.Threading.Channels;
using Contactor.Boards;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Events;

/// <summary>
/// Logs what happens at the board without the app asking: relays turned on or off through the board's web page,
/// its button or its timers (Actor <c>external</c>), changes to the inputs, and the connection being lost for more
/// than 30 s and coming back. An input's change seen as it happened is handed on to its actions
/// (<see cref="InputChangeFeed"/>) once it is logged. The board's status arrives on its read loop; it is queued and
/// handled here one at a time, so the database work never holds up the connection.
/// </summary>
public sealed partial class BoardEventRecorder : BackgroundService
{
    public static readonly TimeSpan ConnectionLostAfter = TimeSpan.FromSeconds(30);

    // How often a lasting disconnect is checked for while there is no new status.
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest wait before a status whose recording failed is tried again. The wait starts at
    /// <see cref="CheckInterval"/> and doubles with each failure in a row, so a database that stays broken fills the
    /// log with one error a minute, not one a second.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>A gap in contact longer than this, e.g. a crash or a board that was off, makes the inputs' times unknown.</summary>
    public static readonly TimeSpan LongGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the time of contact is saved while there is contact: rarely, to spare the SD card. A gap is measured
    /// from it, so it can read up to this much longer than it was.
    /// </summary>
    public static readonly TimeSpan ContactSaveInterval = TimeSpan.FromMinutes(5);

    private readonly IRelayBoard _board;
    private readonly ExpectedChanges _expected;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly ILogger<BoardEventRecorder> _logger;
    private readonly Channel<BoardState> _states = Channel.CreateUnbounded<BoardState>(new UnboundedChannelOptions { SingleReader = true });

    // The last status the board reported while connected; null until the first one after start.
    private BoardState? _known;

    // A status whose recording failed, tried again after a growing wait until it is recorded or a newer status
    // comes: a polled board reports a state only when it changes, so its next status may be long in coming.
    private BoardState? _retry;
    private TimeSpan _retryDelay;

    // Whether the waiting status came before the connection was lost since, and how the recorder stood when it first
    // came: it is then recorded as of that moment, not as the one that brings contact back.
    private bool _retryBeforeLoss;
    private (bool Watching, DateTime? DisconnectedSinceUtc, bool LossLogged) _retryContext;
    private DateTime _retryAtUtc;
    private DateTime? _disconnectedSinceUtc;
    private readonly IBoardConnection? _connection;
    private readonly InputChangeFeed? _inputChanges;

    // The inputs' changes in the status in hand that the app saw happen, handed on once they are saved.
    private readonly List<(int InputId, bool On)> _seenInputChanges = [];

    // Whether the app has had contact since the last gap, and when it last saved that it had.
    private bool _watching;

    // The app's own changes the status in hand matched, to expect again if recording it fails.
    private readonly List<(int RelayId, bool On)> _consumed = [];
    private DateTime? _contactSavedUtc;
    private bool _lossLogged;

    public BoardEventRecorder(
        IRelayBoard board,
        ExpectedChanges expected,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        ILogger<BoardEventRecorder> logger,
        IBoardConnection? connection = null,
        InputChangeFeed? inputChanges = null)
    {
        _board = board;
        _connection = connection;
        _inputChanges = inputChanges;
        _expected = expected;
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _logger = logger;
    }

    // Subscribes here rather than in ExecuteAsync, which .NET runs on a background thread: the first status
    // could arrive before it subscribes, and the next change would then be taken as the starting point.
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _board.StateChanged += OnStateChanged;
        var state = _board.State;
        if (state.IsConnected)
        {
            _states.Writer.TryWrite(state);
        }
        else
        {
            // Not connected yet counts as a disconnect from the start, so a board that never answers is logged too.
            _disconnectedSinceUtc = _clock.UtcNow;
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(CheckInterval);
                try
                {
                    while (await _states.Reader.WaitToReadAsync(wait.Token))
                    {
                        while (_states.Reader.TryRead(out var state))
                        {
                            // The status waiting for its retry first: a change the newer one no longer shows (an input
                            // that closed and opened again) is recorded too. Not one from a board forgotten since.
                            if (_retry is { } waiting && !IsForgotten(state))
                            {
                                await HandleSafelyAsync(() => HandleAsync(waiting, stoppingToken));
                            }

                            await HandleSafelyAsync(() => HandleAsync(state, stoppingToken));
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The check interval passed without a new status.
                }

                // Also while there is no contact: a status from before the loss is recorded as of before it.
                if (_retry is { } retry && _clock.UtcNow >= _retryAtUtc)
                {
                    await HandleSafelyAsync(() => HandleAsync(retry, stoppingToken));
                }

                await HandleSafelyAsync(() => CheckConnectionLostAsync(stoppingToken));
                await HandleSafelyAsync(() => SaveContactAsync(stoppingToken));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            _board.StateChanged -= OnStateChanged;
        }
    }

    private void OnStateChanged(object? sender, BoardState state)
    {
        _states.Writer.TryWrite(state);
    }

    private async Task HandleAsync(BoardState state, CancellationToken cancellationToken)
    {
        // A newer connected status replaces one that failed again: it is compared with the same last recorded status,
        // so it records the failed one's changes that it still shows.
        var retrying = ReferenceEquals(state, _retry);
        var replacing = _retry is not null && !retrying;
        if (!state.IsConnected)
        {
            // The board's state forgotten: its address changed, and another board may be there. Its first status is
            // taken as at start, not compared with the old board's relays, so nothing is logged as switched outside
            // the app for being different, and a status from the old board waiting for its retry is dropped. A lost
            // connection keeps it: it is tried first when contact comes back, while the app's own changes it matched
            // still match it.
            if (IsForgotten(state))
            {
                _known = null;
                _retry = null;
            }
            else if (_retry is not null)
            {
                _retryBeforeLoss = true;
            }

            // Contact was lost after the last status, the same time the warning on the relay screen shows.
            _disconnectedSinceUtc ??= state.LastContactUtc?.UtcDateTime ?? _clock.UtcNow;
            _watching = false;
            return;
        }

        var beforeLoss = retrying && _retryBeforeLoss;
        var context = _retryContext;
        _retry = null;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = _clock.UtcNow;

        // What this status changes, kept so a failure on the way (a read or the save) puts it all back and the next try
        // starts over: the same status again at the next check, or a newer one. The changes, a gap that just ended and a
        // restored connection are recorded then.
        var before = (_known, _disconnectedSinceUtc, _lossLogged, _watching, _contactSavedUtc);
        _consumed.Clear();
        _seenInputChanges.Clear();
        try
        {
            if (beforeLoss)
            {
                // A status from before the connection was lost: recorded as the recorder stood when it came (no gap
                // ended, no contact restored by it), and the loss is still on after it. Its inputs' changes were seen as
                // they happened, but their moment has passed: they are logged and don't act. The status that brings
                // contact back ends the gap itself.
                (_watching, _disconnectedSinceUtc, _lossLogged) = context;
                await RecordAsync(db, state, now, act: false, cancellationToken);
                (_disconnectedSinceUtc, _lossLogged, _watching) = (before._disconnectedSinceUtc, before._lossLogged, false);
                _retryBeforeLoss = false;
            }
            else
            {
                await RecordAsync(db, state, now, act: true, cancellationToken);
                _retryBeforeLoss = false;
            }
        }
        catch
        {
            (_known, _disconnectedSinceUtc, _lossLogged, _watching, _contactSavedUtc) = before;
            if (!retrying)
            {
                // A status new to the recorder: how it stood when it came, for when it is tried again after a loss.
                _retryContext = (before._watching, before._disconnectedSinceUtc, before._lossLogged);
                _retryBeforeLoss = false;
            }

            // The app's own changes this status matched are expected again, or the next try would call them external
            // and clear their runtime, e.g. a relay's auto-off.
            foreach (var (relayId, on) in _consumed)
            {
                _expected.Expect(ChannelMask.Of(relayId), on);
            }

            // A newer status that fails right after the one it replaces failed again keeps the grown wait.
            _retryDelay = retrying || replacing ? TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxRetryDelay.Ticks)) : CheckInterval;
            _retryAtUtc = _clock.UtcNow + _retryDelay;
            _retry = state;
            throw;
        }
    }

    // Not connected and never answered: the board's state forgotten for a new address or another board.
    private static bool IsForgotten(BoardState state)
    {
        return !state.IsConnected && state.LastContactUtc is null;
    }

    private async Task RecordAsync(ContactorDbContext db, BoardState state, DateTime now, bool act, CancellationToken cancellationToken)
    {
        // Contact again, after the start or a lost contact: was the gap long enough that the inputs' times can't be trusted?
        var afterGap = !_watching;
        if (afterGap)
        {
            // A status recorded as of before a lost connection ended the gap when it came, not now: there is no
            // contact now.
            var contactCame = act ? now : state.LastContactUtc?.UtcDateTime ?? now;
            await StartWatchingAsync(db, contactCame, saveContact: act, cancellationToken);
        }

        if (_disconnectedSinceUtc is { } since)
        {
            if (_lossLogged)
            {
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    Actor = EventActors.System,
                    Action = EventActions.ConnectionRestored,
                    Details = JsonSerializer.Serialize(new { afterSeconds = (int)(now - since).TotalSeconds }),
                });
            }

            _disconnectedSinceUtc = null;
            _lossLogged = false;
        }

        // The first status after start has nothing to compare the relays with: what happened before is unknown. After
        // a reconnect it is compared with the last status before the loss, so changes made meanwhile are logged.
        if (_known is { } known)
        {
            await RecordRelayChangesAsync(db, known.Relays, state.Relays, now, state.LastContactUtc?.UtcDateTime ?? now, cancellationToken);
            RecordInputChanges(db, known.Inputs, state.Inputs, now, noticed: afterGap);
        }
        else
        {
            // At start the inputs are compared with what the event log last says about each: one that changed while
            // the app was down is logged now, as noticed, so its "since" reads unknown.
            RecordInputChanges(db, await LastLoggedInputsAsync(db, state.Inputs, cancellationToken), state.Inputs, now, noticed: true);
            await GiveOwnerlessRelaysAutoOffAsync(db, state.Relays, now, cancellationToken);
        }

        _known = state;
        if (db.ChangeTracker.HasChanges())
        {
            await RelayRuns.SaveAsync(db, cancellationToken);
            _changes.Notify();
        }

        // Only once logged, so the input's change comes before what it did in Events, and a failed save doesn't act
        // twice when the next status records the change again.
        foreach (var (input, on) in act ? _seenInputChanges : [])
        {
            _inputChanges?.Enqueue(input, on);
        }
    }

    // Every input on the board, each logged on its own. A change the app only noticed after a gap is marked so: it
    // happened at a time nobody knows.
    private void RecordInputChanges(ContactorDbContext db, ChannelMask before, ChannelMask after, DateTime now, bool noticed)
    {
        for (var input = 1; input <= _board.Capabilities.InputCount; input++)
        {
            if (before.Contains(input) != after.Contains(input))
            {
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    InputId = input,
                    Actor = EventActors.External,
                    Action = EventActions.InputChanged,
                    Details = noticed
                        ? JsonSerializer.Serialize(new { on = after.Contains(input), noticed = true })
                        : JsonSerializer.Serialize(new { on = after.Contains(input) }),
                });
                if (!noticed)
                {
                    _seenInputChanges.Add((input, after.Contains(input)));
                }
            }
        }
    }

    // The inputs as the event log last left them; an input never logged counts as matching the status in hand.
    private async Task<ChannelMask> LastLoggedInputsAsync(ContactorDbContext db, ChannelMask current, CancellationToken cancellationToken)
    {
        var lastIds = db.EventLog
            .Where(e => e.Action == EventActions.InputChanged && e.InputId != null)
            .GroupBy(e => e.InputId)
            .Select(g => g.Max(e => e.Id));
        var last = await db.EventLog.AsNoTracking()
            .Where(e => lastIds.Contains(e.Id))
            .Select(e => new { InputId = e.InputId!.Value, e.Details })
            .ToListAsync(cancellationToken);
        var logged = last.ToDictionary(e => e.InputId, e => EventText.InputOn(e.Details));
        var inputs = ChannelMask.None;
        for (var input = 1; input <= _board.Capabilities.InputCount; input++)
        {
            var on = logged.GetValueOrDefault(input) ?? current.Contains(input);
            if (on)
            {
                inputs |= ChannelMask.Of(input);
            }
        }

        return inputs;
    }

    // Measures the gap from the last contact (before a restart: the saved one) to when contact came back, and saves when
    // a long gap ended. A short gap, e.g. a reboot or an upgrade, keeps the inputs' times: it's taken that nothing
    // changed and changed back. For a status recorded as of before a lost connection (saveContact false), contact came
    // back when that status came: the gap ends then, but a later end saved meanwhile stays, and no time of contact is
    // saved, since there is no contact now and a restart during the loss would read the gap as shorter than it was.
    private async Task StartWatchingAsync(ContactorDbContext db, DateTime now, bool saveContact, CancellationToken cancellationToken)
    {
        var saved = await db.BoardWatchStates.SingleOrDefaultAsync(cancellationToken);
        if (saved is null)
        {
            saved = new BoardWatchState();
            db.BoardWatchStates.Add(saved);
        }

        // After a lost contact, the later of the board's last answer and the app's own last save of the time of contact.
        var lastContact = _known is null ? saved.LastContactUtc : Later(_disconnectedSinceUtc, _contactSavedUtc);
        // A clock that went back (a Raspberry Pi has no clock of its own: after a power cut it starts from its last
        // saved time until it reaches a time server) can't measure the gap, so it counts as long.
        if (lastContact is not { } last || now < last || now - last > LongGap)
        {
            saved.LongGapEndedUtc = saveContact || saved.LongGapEndedUtc is not { } ended || now > ended ? now : ended;
        }

        if (saveContact)
        {
            saved.LastContactUtc = now;
            _contactSavedUtc = now;
        }

        _watching = true;
    }

    private static DateTime? Later(DateTime? a, DateTime? b)
    {
        return a is null ? b : b is null ? a : a > b ? a : b;
    }

    // Every few minutes while there is contact, so the next start can tell how long the app was away.
    private async Task SaveContactAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (!_watching || !_board.State.IsConnected || (_contactSavedUtc is { } saved && now - saved < ContactSaveInterval))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.BoardWatchStates.SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return;
        }

        state.LastContactUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        _contactSavedUtc = now;
    }

    private async Task RecordRelayChangesAsync(
        ContactorDbContext db,
        ChannelMask before,
        ChannelMask after,
        DateTime now,
        DateTime receivedUtc,
        CancellationToken cancellationToken)
    {
        for (var relayId = 1; relayId <= _board.Capabilities.RelayCount; relayId++)
        {
            var wasOn = before.Contains(relayId);
            var isOn = after.Contains(relayId);
            if (wasOn == isOn)
            {
                continue;
            }

            if (_expected.TryConsume(relayId, isOn, receivedUtc))
            {
                _consumed.Add((relayId, isOn));
                continue;
            }

            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                RelayId = relayId,
                Actor = EventActors.External,
                Action = isOn ? EventActions.On : EventActions.Off,
            });

            if (isOn)
            {
                // Nobody in the app turned it on: it gets the relay's own auto-off all the same, so nothing runs for
                // ever by accident.
                if (await OwnAutoOffAtAsync(db, relayId, now, cancellationToken) is { } own)
                {
                    await RelayRuns.StartOutsideAsync(db, relayId, now, own.AutoOffAt, cancellationToken);
                }
            }
            else
            {
                // Whoever turned it on no longer has it on: the status line must not keep their name, and auto-off
                // must not act on it.
                await RelayRuns.EndOutsideAsync(db, relayId, cancellationToken);
            }
        }
    }

    // At start, and when the board's state was forgotten for a new address: a relay that is on with nobody's run gets
    // its own auto-off from now, as if it had just come on outside the app. One with a run (a person's, a schedule's)
    // keeps it.
    private async Task GiveOwnerlessRelaysAutoOffAsync(ContactorDbContext db, ChannelMask relays, DateTime now, CancellationToken cancellationToken)
    {
        var on = relays.Channels.Where(r => r <= _board.Capabilities.RelayCount).ToList();
        if (on.Count == 0)
        {
            return;
        }

        var withRun = await db.RelayRuntimes.AsNoTracking()
            .Where(r => on.Contains(r.RelayId) && r.OnSinceUtc != null)
            .Select(r => r.RelayId)
            .ToListAsync(cancellationToken);
        foreach (var relayId in on.Except(withRun))
        {
            if (await OwnAutoOffAtAsync(db, relayId, now, cancellationToken) is { } own)
            {
                await RelayRuns.GiveOwnAutoOffAsync(db, relayId, now, own.AutoOffAt, cancellationToken);
            }
        }
    }

    // A relay on outside the app gets the relay's own "Turn off automatically after", counted from now; null for a
    // relay in a group, which gets none of its own: its group's run applies (GroupSequencer).
    private static async Task<OwnAutoOff?> OwnAutoOffAtAsync(ContactorDbContext db, int relayId, DateTime now, CancellationToken cancellationToken)
    {
        if (await db.RelayGroupMembers.AnyAsync(m => m.RelayId == relayId, cancellationToken))
        {
            return null;
        }

        var maxOnMinutes = await db.Relays.Where(r => r.Id == relayId).Select(r => r.MaxOnMinutes).SingleOrDefaultAsync(cancellationToken);
        return new OwnAutoOff(maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null);
    }

    private sealed record OwnAutoOff(DateTime? AutoOffAt);

    private async Task CheckConnectionLostAsync(CancellationToken cancellationToken)
    {
        // A new installation has no address for its board until someone enters one: nothing is lost before that, so
        // the count starts once there is an address.
        if (_disconnectedSinceUtc is not null && _connection is { Endpoint.HasHost: false })
        {
            _disconnectedSinceUtc = _clock.UtcNow;
            return;
        }

        if (_disconnectedSinceUtc is not { } since || _lossLogged || _clock.UtcNow - since < ConnectionLostAfter)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = since,
            Actor = EventActors.System,
            Action = EventActions.ConnectionLost,
        });
        await db.SaveChangesAsync(cancellationToken);
        _lossLogged = true;
        _changes.Notify();
    }

    private async Task HandleSafelyAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
#pragma warning disable CA1031 // A failed write must not stop the recorder; it is logged and the next status goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record a board event")]
    private partial void LogFailed(Exception exception);
}
