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
    private DateTime? _disconnectedSinceUtc;
    private readonly IBoardConnection? _connection;
    private readonly InputChangeFeed? _inputChanges;

    // The inputs' changes in the status in hand that the app saw happen, handed on once they are saved.
    private readonly List<(int InputId, bool On)> _seenInputChanges = [];

    // Whether the app has had contact since the last gap, and when it last saved that it had.
    private bool _watching;

    // The app's own changes the status in hand matched, to expect again if recording it fails.
    private readonly List<(int RelayId, bool On)> _consumed = [];

    // The failed ONs the status in hand was credited to, to remember again if recording it fails.
    private readonly List<FailedOn> _consumedFailures = [];
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
                            await HandleSafelyAsync(() => HandleAsync(state, stoppingToken));
                        }
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The check interval passed without a new status.
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
        if (!state.IsConnected)
        {
            // Contact was lost after the last status, the same time the warning on the relay screen shows.
            _disconnectedSinceUtc ??= state.LastContactUtc?.UtcDateTime ?? _clock.UtcNow;
            _watching = false;
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var now = _clock.UtcNow;

        // What this status changes, kept so a failure on the way (a read or the save) puts it all back and the next
        // status starts over: the changes, a gap that just ended and a restored connection are recorded then.
        var before = (_known, _disconnectedSinceUtc, _lossLogged, _watching, _contactSavedUtc);
        _consumed.Clear();
        _consumedFailures.Clear();
        _seenInputChanges.Clear();
        try
        {
            await RecordAsync(db, state, now, cancellationToken);
        }
        catch
        {
            (_known, _disconnectedSinceUtc, _lossLogged, _watching, _contactSavedUtc) = before;

            // The app's own changes this status matched are expected again, or the next try would call them external
            // and clear their runtime, e.g. a relay's auto-off.
            foreach (var (relayId, on) in _consumed)
            {
                _expected.Expect(ChannelMask.Of(relayId), on);
            }

            foreach (var note in _consumedFailures)
            {
                _expected.PutBackFailedOn(note);
            }

            throw;
        }
    }

    private async Task RecordAsync(ContactorDbContext db, BoardState state, DateTime now, CancellationToken cancellationToken)
    {
        // Contact again, after the start or a lost contact: was the gap long enough that the inputs' times can't be trusted?
        var afterGap = !_watching;
        if (afterGap)
        {
            await StartWatchingAsync(db, now, cancellationToken);
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
        }

        _known = state;
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
            _changes.Notify();
        }

        // Only once logged, so the input's change comes before what it did in Events, and a failed save doesn't act
        // twice when the next status records the change again.
        foreach (var (input, on) in _seenInputChanges)
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

    // Measures the gap since the last contact (before a restart: the saved one) and saves when a long gap ended. A short
    // gap, e.g. a reboot or an upgrade, keeps the inputs' times: it's taken that nothing changed and changed back.
    private async Task StartWatchingAsync(ContactorDbContext db, DateTime now, CancellationToken cancellationToken)
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
            saved.LongGapEndedUtc = now;
        }

        saved.LastContactUtc = now;
        _contactSavedUtc = now;
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

            var runtime = await db.RelayRuntimes.SingleOrDefaultAsync(r => r.RelayId == relayId, cancellationToken);

            // Someone pressed ON and was told it failed, but the board switched it on: it is their ON, with their
            // auto-off, so a fan heater doesn't run until someone notices.
            if (isOn && _expected.TakeFailedOn(relayId, receivedUtc) is { } failed)
            {
                _consumedFailures.Add(failed);
                await CreditFailedOnAsync(db, relayId, failed.Actor, runtime, now, cancellationToken);
                continue;
            }

            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                RelayId = relayId,
                Actor = EventActors.External,
                Action = isOn ? EventActions.On : EventActions.Off,
            });

            // Whoever turned it on in the app no longer has it on: the status line must not keep their name, and
            // auto-off must not act on it.
            if (runtime is not null)
            {
                runtime.OnSinceUtc = null;
                runtime.AutoOffAtUtc = null;
                runtime.TurnedOnBy = null;
            }
        }
    }

    // As RelayControl records a successful ON, logged with Details {"afterFailedCommand":true}.
    private static async Task CreditFailedOnAsync(
        ContactorDbContext db,
        int relayId,
        string actor,
        RelayRuntime? runtime,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var maxOnMinutes = await db.Relays.Where(r => r.Id == relayId).Select(r => r.MaxOnMinutes).SingleOrDefaultAsync(cancellationToken);
        if (runtime is null)
        {
            runtime = new RelayRuntime { RelayId = relayId };
            db.RelayRuntimes.Add(runtime);
        }

        runtime.OnSinceUtc = now;
        runtime.TurnedOnBy = actor;
        runtime.AutoOffAtUtc = maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = now,
            RelayId = relayId,
            Actor = actor,
            Action = EventActions.On,
            Details = """{"afterFailedCommand":true}""",
        });
    }

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
