using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// Turns relays off when their auto-off time has passed: every 15 s it looks for a <see cref="RelayRuntime"/> whose
/// <see cref="RelayRuntime.AutoOffAtUtc"/> is due, sends OFF if the board says the relay is still on, logs it as
/// Actor <c>auto-off</c>, and clears the runtime. Only a person's ON sets an auto-off time (and a new limit only
/// moves it for such a relay), so relays a schedule turned on are never touched. While the board is disconnected nothing happens; a time that passed meanwhile (also before
/// a restart) is handled at the first check after the connection is back.
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
    /// Gives every relay that is on with nobody's run its own auto-off, then handles every auto-off that is due now.
    /// Returns the relays it turned off.
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
        if (await GiveOwnerlessRelaysAutoOffAsync(db, state, now, cancellationToken))
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
            if (state.IsOn(runtime.RelayId))
            {
                var mask = _board.Capabilities.Relay(runtime.RelayId);
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
                    RelayId = runtime.RelayId,
                    Actor = EventActors.AutoOff,
                    Action = EventActions.Off,
                    Details = minutes is null ? null : JsonSerializer.Serialize(new { afterMinutes = minutes }),
                });
                turnedOff.Add(runtime.RelayId);
            }

            // Off already (turned off some other way): nothing to send, but the plan is stale.
            runtime.OnSinceUtc = null;
            runtime.AutoOffAtUtc = null;
            runtime.TurnedOnBy = null;
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
            _changes.Notify();
        }

        return turnedOff;
    }

    // A relay can be on with nobody's run without the app seeing it come on, e.g. after an OFF the board was sent but
    // didn't carry out. It gets the relay's own auto-off from now, as one that came on outside the app, so nothing runs
    // for ever by accident. A relay in a group is left to its group (GroupPlanner.GiveOwnerlessAutoOff). Each update
    // only applies while the relay still has no run, so a person's or a schedule's ON saved meanwhile keeps theirs.
    private async Task<bool> GiveOwnerlessRelaysAutoOffAsync(ContactorDbContext db, BoardState state, DateTime now, CancellationToken cancellationToken)
    {
        var on = state.Relays.Channels.Where(r => r <= _board.Capabilities.RelayCount).ToList();
        if (on.Count == 0)
        {
            return false;
        }

        var members = db.RelayGroupMembers.Select(m => m.RelayId);
        var ownerless = await db.Relays.AsNoTracking()
            .Where(r => on.Contains(r.Id) && !members.Contains(r.Id))
            .Where(r => !db.RelayRuntimes.Any(x => x.RelayId == r.Id && x.OnSinceUtc != null))
            .Select(r => new { r.Id, r.MaxOnMinutes })
            .ToListAsync(cancellationToken);
        var changed = false;
        foreach (var relay in ownerless)
        {
            var autoOffAt = relay.MaxOnMinutes is { } minutes ? now.AddMinutes(minutes) : (DateTime?)null;
            var updated = await db.RelayRuntimes
                .Where(r => r.RelayId == relay.Id && r.OnSinceUtc == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(r => r.OnSinceUtc, now)
                        .SetProperty(r => r.TurnedOnBy, EventActors.External)
                        .SetProperty(r => r.AutoOffAtUtc, autoOffAt),
                    cancellationToken);
            if (updated > 0)
            {
                LogOwnerless(relay.Id);
                changed = true;
            }
        }

        return changed;
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
}
