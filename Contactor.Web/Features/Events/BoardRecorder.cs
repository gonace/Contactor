using System.Text.Json;
using System.Threading.Channels;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Inputs;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Events;

/// <summary>
/// Logs what happens at one board without the app asking (see <see cref="BoardEventRecorder"/>): its relays turned on
/// or off outside the app, its inputs' changes, and its connection lost and back, each event with this board's id. Its
/// own known state, disconnect clock, retry and contact times (<see cref="BoardRuntime"/>), and its own expected
/// changes (<see cref="LiveBoard.Expected"/>). Made for each live board, subscribed before its connection starts, so
/// no status goes unseen; statuses are queued and handled one at a time on the board's own loop.
/// </summary>
public sealed partial class BoardRecorder : IBoardPart, IAsyncDisposable
{
    private readonly LiveBoard _board;
    private readonly ExpectedChanges _expected;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly ILogger<BoardRecorder> _logger;
    private readonly Channel<BoardState> _states = Channel.CreateUnbounded<BoardState>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _ended = new();
    private int _disposed;


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
    private readonly InputChangeFeed? _inputChanges;

    // The inputs' changes in the status in hand that the app saw happen, handed on once they are saved.
    private readonly List<(int InputId, bool On)> _seenInputChanges = [];

    // Whether the app has had contact since the last gap, and when it last saved that it had.
    private bool _watching;

    // The app's own changes the status in hand matched, by channel, to expect again if recording it fails.
    private readonly List<(int Channel, bool On)> _consumed = [];
    private DateTime? _contactSavedUtc;
    private bool _lossLogged;

    internal BoardRecorder(
        LiveBoard board,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        ILogger<BoardRecorder> logger,
        InputChangeFeed? inputChanges)
    {
        _board = board;
        _expected = board.Expected;
        _inputChanges = inputChanges;
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _logger = logger;

        // From its making, before the board's connection starts: the first status can't come before it listens, or
        // the next change would be taken as the starting point.
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
    }

    /// <inheritdoc/>
    public int BoardId => _board.BoardId;

    /// <summary>Stops listening and ends the loop; a status still queued isn't recorded.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _board.StateChanged -= OnStateChanged;
        _states.Writer.TryComplete();
        await _ended.CancelAsync();
    }

    /// <summary>The board's loop: records each status, and checks for a lasting disconnect every second.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ended.Token);
        var stoppingToken = linked.Token;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(BoardEventRecorder.CheckInterval);
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

                if (_states.Reader.Completion.IsCompleted)
                {
                    // The watch ended.
                    return;
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
            // Stopping, or the board's watch ended.
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
            foreach (var (channel, on) in _consumed)
            {
                _expected.Expect(ChannelMask.Of(channel), on);
            }

            // A newer status that fails right after the one it replaces failed again keeps the grown wait.
            _retryDelay = retrying || replacing ? TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, BoardEventRecorder.MaxRetryDelay.Ticks)) : BoardEventRecorder.CheckInterval;
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
                    BoardId = BoardId,
                    Actor = EventActors.System,
                    Action = EventActions.ConnectionRestored,
                    Details = JsonSerializer.Serialize(new { afterSeconds = (int)(now - since).TotalSeconds }),
                });
            }

            _disconnectedSinceUtc = null;
            _lossLogged = false;
        }

        // The board's state goes by channel; the events and runs by the relays' and inputs' ids.
        var channels = await BoardChannels.LoadAsync(db, BoardId, cancellationToken);

        // The first status after start has nothing to compare the relays with: what happened before is unknown. After
        // a reconnect it is compared with the last status before the loss, so changes made meanwhile are logged.
        if (_known is { } known)
        {
            await RecordRelayChangesAsync(db, channels, known.Relays, state.Relays, now, state.LastContactUtc?.UtcDateTime ?? now, cancellationToken);
            RecordInputChanges(db, channels, BoardId, known.Inputs, state.Inputs, now, noticed: afterGap);
        }
        else
        {
            // At start the inputs are compared with what the event log last says about each: one that changed while
            // the app was down is logged now, as noticed, so its "since" reads unknown.
            RecordInputChanges(db, channels, BoardId, await LastLoggedInputsAsync(db, channels, state.Inputs, cancellationToken), state.Inputs, now, noticed: true);
            await GiveOwnerlessRelaysAutoOffAsync(db, channels, state.Relays, now, cancellationToken);
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

    // Every input on the board, by its channel, each logged on its own. A change the app only noticed after a gap is
    // marked so: it happened at a time nobody knows.
    private void RecordInputChanges(ContactorDbContext db, BoardChannels channels, int boardId, ChannelMask before, ChannelMask after, DateTime now, bool noticed)
    {
        for (var channel = 1; channel <= _board.Capabilities.InputCount; channel++)
        {
            if (before.Contains(channel) != after.Contains(channel) && channels.InputAt(channel) is { } inputId)
            {
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    BoardId = boardId,
                    InputId = inputId,
                    Actor = EventActors.External,
                    Action = EventActions.InputChanged,
                    Details = noticed
                        ? JsonSerializer.Serialize(new { on = after.Contains(channel), noticed = true })
                        : JsonSerializer.Serialize(new { on = after.Contains(channel) }),
                });
                if (!noticed)
                {
                    _seenInputChanges.Add((inputId, after.Contains(channel)));
                }
            }
        }
    }

    // The inputs as the event log last left them, by channel; an input never logged counts as matching the status in hand.
    private async Task<ChannelMask> LastLoggedInputsAsync(ContactorDbContext db, BoardChannels channels, ChannelMask current, CancellationToken cancellationToken)
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
        for (var channel = 1; channel <= _board.Capabilities.InputCount; channel++)
        {
            var on = (channels.InputAt(channel) is { } inputId ? logged.GetValueOrDefault(inputId) : null) ?? current.Contains(channel);
            if (on)
            {
                inputs |= ChannelMask.Of(channel);
            }
        }

        return inputs;
    }

    // Measures the gap from the last contact (before a restart: the saved one) to when contact came back, and saves when
    // a long gap ended. A short gap, e.g. a reboot or an upgrade, keeps the inputs' times: it's taken that nothing
    // changed and changed back. For a status recorded as of before a lost connection (saveContact false), contact came
    // back when that status came: the gap ends then. No time of contact is saved for it: there is no contact now, and a
    // restart during the loss would read the gap as shorter than it was.
    private async Task StartWatchingAsync(ContactorDbContext db, DateTime now, bool saveContact, CancellationToken cancellationToken)
    {
        // Kept on the board's runtime row, added when it has none yet.
        var saved = await db.BoardRuntimeAsync(BoardId, cancellationToken);

        // After a lost contact, the later of the board's last answer and the app's own last save of the time of contact.
        var lastContact = _known is null ? saved?.LastContactUtc : Later(_disconnectedSinceUtc, _contactSavedUtc);
        // A clock that went back (a Raspberry Pi has no clock of its own: after a power cut it starts from its last
        // saved time until it reaches a time server) can't measure the gap, so it counts as long.
        if (saved is not null && (lastContact is not { } last || now < last || now - last > BoardEventRecorder.LongGap))
        {
            saved.LongGapEndedUtc = now;
        }

        if (saveContact)
        {
            saved?.LastContactUtc = now;
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
        if (!_watching || !_board.State.IsConnected || (_contactSavedUtc is { } saved && now - saved < BoardEventRecorder.ContactSaveInterval))
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var state = await db.BoardRuntimeAsync(BoardId, cancellationToken);
        if (state is null)
        {
            return;
        }

        state.LastContactUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        _contactSavedUtc = now;
    }

    // By channel: the expectations are the board's; the events and runs are the relays'.
    private async Task RecordRelayChangesAsync(
        ContactorDbContext db,
        BoardChannels channels,
        ChannelMask before,
        ChannelMask after,
        DateTime now,
        DateTime receivedUtc,
        CancellationToken cancellationToken)
    {
        for (var channel = 1; channel <= _board.Capabilities.RelayCount; channel++)
        {
            var wasOn = before.Contains(channel);
            var isOn = after.Contains(channel);
            if (wasOn == isOn)
            {
                continue;
            }

            if (_expected.TryConsume(channel, isOn, receivedUtc))
            {
                _consumed.Add((channel, isOn));
                continue;
            }

            // Every channel has its relay once the board's rows are seeded; there is nothing to log for one without.
            if (channels.RelayAt(channel) is not { } relayId)
            {
                continue;
            }

            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                BoardId = BoardId,
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
    private async Task GiveOwnerlessRelaysAutoOffAsync(ContactorDbContext db, BoardChannels channels, ChannelMask relays, DateTime now, CancellationToken cancellationToken)
    {
        var on = channels.RelayIds(relays & _board.Capabilities.AllRelays).ToList();
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
        if (_disconnectedSinceUtc is not null && !_board.Endpoint.HasHost)
        {
            _disconnectedSinceUtc = _clock.UtcNow;
            return;
        }

        if (_disconnectedSinceUtc is not { } since || _lossLogged || _clock.UtcNow - since < BoardEventRecorder.ConnectionLostAfter)
        {
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = since,
            BoardId = BoardId,
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
            LogFailed(ex, BoardId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not record an event of relay board {BoardId}")]
    private partial void LogFailed(Exception exception, int boardId);
}
