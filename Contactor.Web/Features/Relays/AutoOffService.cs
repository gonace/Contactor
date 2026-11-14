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
/// board shows off loses a run left on it (ended here, at every check). While the board is disconnected nothing
/// happens; a time that passed meanwhile (also before a restart) is handled at the first check after the connection
/// is back.
/// </summary>
public sealed partial class AutoOffService : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IRelayBoard _board;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly ExpectedChanges _expected;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AutoOffService> _logger;

    public AutoOffService(
        IRelayBoard board,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        ExpectedChanges expected,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILogger<AutoOffService> logger)
    {
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _expected = expected;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gives every relay that is on with nobody's run its own auto-off, handles every auto-off that is due now, and
    /// ends the run of every relay the board shows off. Returns the relays it turned off.
    /// </summary>
    public async Task<IReadOnlyList<int>> CheckAsync(CancellationToken cancellationToken)
    {
        var state = _board.State;
        if (!state.IsConnected)
        {
            return [];
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var channels = await BoardChannels.LoadAsync(db, cancellationToken);
        if (await GiveOwnerlessRelaysAutoOffAsync(db, channels, state, now, cancellationToken))
        {
            _changes.Notify();
        }

        var due = await db.RelayRuntimes
            .Where(r => r.AutoOffAtUtc != null && r.AutoOffAtUtc <= now)
            .OrderBy(r => r.RelayId)
            .ToListAsync(cancellationToken);

        List<int> turnedOff = [];
        foreach (var runtime in due)
        {
            // The board's state and the command go by the relay's channel; a relay the board doesn't have isn't on.
            if (OnBoard(channels, runtime.RelayId) is { } channel && state.IsOn(channel))
            {
                var sent = _clock.UtcNow;
                var mask = _board.Capabilities.Relay(channel);
                _expected.Expect(mask, on: false);
                try
                {
                    await _board.TurnOffAsync(mask, cancellationToken);
                }
                catch (BoardNotConnectedException)
                {
                    // Lost the board mid-way: the rest waits for the next check. Nothing is logged, so whatever the
                    // board did shows as an external change.
                    _expected.Forget(mask, on: false);
                    break;
                }

                var minutes = runtime.OnSinceUtc is { } since ? (int)Math.Round((runtime.AutoOffAtUtc!.Value - since).TotalMinutes) : (int?)null;
                db.EventLog.Add(new EventLogEntry
                {
                    TimestampUtc = now,
                    BoardId = await db.FirstBoardIdAsync(cancellationToken),
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
        if (_board.State.IsConnected)
        {
            await EndRunsOfOffRelaysAsync(db, channels, state, [.. due.Select(r => r.RelayId)], cancellationToken);
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
    private async Task EndRunsOfOffRelaysAsync(ContactorDbContext db, BoardChannels channels, BoardState state, HashSet<int> handled, CancellationToken cancellationToken)
    {
        var off = Enumerable.Range(1, _board.Capabilities.RelayCount)
            .Where(c => !state.IsOn(c) && !_expected.IsPending(c, on: true))
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
        foreach (var run in stale.Where(r => channels.RelayChannel(r.RelayId) is { } c && !_expected.IsPending(c, on: true) && !_board.State.IsOn(c)))
        {
            await RelayRuns.EndOutsideAsync(db, run.RelayId, cancellationToken);
            LogStaleRun(run.RelayId);
        }
    }

    // A relay can be on with nobody's run without the app seeing it come on, e.g. after an OFF the board was sent but
    // didn't carry out. It gets the relay's own auto-off from now, as one that came on outside the app, so nothing runs
    // for ever by accident. A relay in a group is left to its group (GroupPlanner.GiveOwnerlessAutoOff). Each update
    // only applies while the relay still has no run, so a person's or a schedule's ON saved meanwhile keeps theirs.
    private async Task<bool> GiveOwnerlessRelaysAutoOffAsync(ContactorDbContext db, BoardChannels channels, BoardState state, DateTime now, CancellationToken cancellationToken)
    {
        var on = channels.RelayIds(state.Relays & _board.Capabilities.AllRelays).ToList();
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
            if (!_board.State.IsOn(relay.Channel))
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
    private int? OnBoard(BoardChannels channels, int relayId)
    {
        return channels.RelayChannel(relayId) is { } channel && channel <= _board.Capabilities.RelayCount ? channel : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(CheckInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // One failed check must not stop auto-off for good; it is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCheckFailed(ex);
                await Task.Delay(CheckInterval, _timeProvider, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Auto-off check failed")]
    private partial void LogCheckFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay {RelayId} is on with nobody's run: it gets its own auto-off")]
    private partial void LogOwnerless(int relayId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relay {RelayId} is off but has a run: ending it")]
    private partial void LogStaleRun(int relayId);
}
