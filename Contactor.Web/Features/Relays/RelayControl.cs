using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// Turns relays on and off for a person: sends the command to the board first, and only once it went out records
/// who did it (<see cref="RelayRuntime"/>, auto-off time) and logs the event. If the board is not connected the
/// command fails with <see cref="BoardNotConnectedException"/> and nothing is recorded. A relay in a group is only
/// turned on and off through its group (<see cref="GroupSequencer"/>), which keeps the group's order and interlock.
/// </summary>
public sealed class RelayControl
{
    private readonly IRelayBoard _board;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly ExpectedChanges _expected;
    private readonly GroupSequencer _groups;

    public RelayControl(
        IRelayBoard board,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        ExpectedChanges expected,
        GroupSequencer groups)
    {
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _expected = expected;
        _groups = groups;
    }

    public async Task TurnOnAsync(int relayId, string userEmail, CancellationToken cancellationToken)
    {
        var mask = _board.Capabilities.Relay(relayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        await EnsureNotInGroupAsync(mask, cancellationToken);
        await SendAsync(mask, on: true, cancellationToken);

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var relay = await db.Relays.SingleAsync(r => r.Id == relayId, cancellationToken);
        var runtime = await GetRuntimeAsync(db, relayId, cancellationToken);
        runtime.OnSinceUtc = now;
        runtime.TurnedOnBy = userEmail;
        runtime.AutoOffAtUtc = relay.MaxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
        db.EventLog.Add(new EventLogEntry { TimestampUtc = now, RelayId = relayId, Actor = userEmail, Action = EventActions.On });
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
    }

    public async Task TurnOffAsync(int relayId, string userEmail, CancellationToken cancellationToken)
    {
        var mask = _board.Capabilities.Relay(relayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        await EnsureNotInGroupAsync(mask, cancellationToken);
        await SendAsync(mask, on: false, cancellationToken);

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        Clear(await GetRuntimeAsync(db, relayId, cancellationToken));
        db.EventLog.Add(new EventLogEntry { TimestampUtc = now, RelayId = relayId, Actor = userEmail, Action = EventActions.Off });
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
    }

    /// <summary>
    /// "All off": turns every relay outside a group off with one command and logs one event for each that was on, then
    /// stops every group in its own order, with its run-on times.
    /// </summary>
    public async Task TurnAllOffAsync(string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (!_board.State.IsConnected)
        {
            throw new BoardNotConnectedException();
        }

        var single = _board.Capabilities.AllRelays.Except(await _groups.MemberMaskAsync(cancellationToken));
        var wasOn = _board.State.Relays & single;
        if (!single.IsEmpty)
        {
            _expected.Expect(wasOn, on: false);
            try
            {
                await _board.TurnOffAsync(single, cancellationToken);
            }
            catch
            {
                // A failed command logs nothing: whatever the board did shows as an external change.
                _expected.Forget(wasOn, on: false);
                throw;
            }
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var runtime in await db.RelayRuntimes.ToListAsync(cancellationToken))
        {
            Clear(runtime);
        }

        foreach (var relayId in wasOn.Channels)
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                RelayId = relayId,
                Actor = userEmail,
                Action = EventActions.Off,
                Details = """{"allOff":true}""",
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        await _groups.StopAllAsync(userEmail, cancellationToken);
    }

    // Expects the change, then sends it. A command that fails logs nothing, so its expectation goes too: whatever the
    // board did then shows as an external change rather than vanishing.
    private async Task SendAsync(ChannelMask mask, bool on, CancellationToken cancellationToken)
    {
        _expected.Expect(mask, on);
        try
        {
            if (on)
            {
                await _board.TurnOnAsync(mask, cancellationToken);
            }
            else
            {
                await _board.TurnOffAsync(mask, cancellationToken);
            }
        }
        catch
        {
            _expected.Forget(mask, on);
            throw;
        }
    }

    private async Task EnsureNotInGroupAsync(ChannelMask mask, CancellationToken cancellationToken)
    {
        if ((await _groups.MemberMaskAsync(cancellationToken)).Overlaps(mask))
        {
            throw new RelayInGroupException();
        }
    }

    private static async Task<RelayRuntime> GetRuntimeAsync(ContactorDbContext db, int relayId, CancellationToken cancellationToken)
    {
        var runtime = await db.RelayRuntimes.SingleOrDefaultAsync(r => r.RelayId == relayId, cancellationToken);
        if (runtime is null)
        {
            runtime = new RelayRuntime { RelayId = relayId };
            db.RelayRuntimes.Add(runtime);
        }

        return runtime;
    }

    private static void Clear(RelayRuntime runtime)
    {
        runtime.OnSinceUtc = null;
        runtime.AutoOffAtUtc = null;
        runtime.TurnedOnBy = null;
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
