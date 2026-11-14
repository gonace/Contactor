using System.Text.Json;
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

    /// <summary>
    /// Turns a relay on for a person (their e-mail) or an input (<see cref="EventActors.ForInput"/>), as a press of its
    /// button: with the relay's auto-off.
    /// </summary>
    public async Task TurnOnAsync(int relayId, string actor, CancellationToken cancellationToken)
    {
        var mask = _board.Capabilities.Relay(relayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureNotInGroupAsync(mask, cancellationToken);
        await SendAsync(mask, on: true, cancellationToken);

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var maxOnMinutes = await db.Relays.Where(r => r.Id == relayId).Select(r => r.MaxOnMinutes).SingleAsync(cancellationToken);
        await RelayRuns.StartAsync(db, relayId, actor, now, maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null, cancellationToken);
        db.EventLog.Add(new EventLogEntry { TimestampUtc = now, BoardId = await db.FirstBoardIdAsync(cancellationToken), RelayId = relayId, Actor = actor, Action = EventActions.On });
        await RelayRuns.SaveAsync(db, cancellationToken);
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
        var mask = _board.Capabilities.Relay(relayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (_board.Capabilities.Pulse is not { } pulse)
        {
            throw new NotSupportedException("This board can't pulse a relay.");
        }

        if (length > MaxPulse || !pulse.CanDo(length))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The board can't pulse for that long, or it is longer than a try.");
        }

        await EnsureNotInGroupAsync(mask, cancellationToken);
        _expected.Expect(mask, on: true);

        // The board's own OFF comes after the pulse, and on a board the app polls up to a poll or two later.
        _expected.Expect(mask, on: false, length + PulseOffWait);
        try
        {
            await _board.PulseAsync(mask, length, cancellationToken);
        }
        catch
        {
            // As for ON: a failed or cancelled pulse logs nothing, and whatever the board did shows as an external change.
            _expected.Forget(mask, on: true);
            _expected.Forget(mask, on: false);
            throw;
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await RelayRuns.StartAsync(db, relayId, actor, now, now + length + PulseBackstop, cancellationToken);
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = now,
            BoardId = await db.FirstBoardIdAsync(cancellationToken),
            RelayId = relayId,
            Actor = actor,
            Action = EventActions.Pulse,
            Details = JsonSerializer.Serialize(new { milliseconds = (int)length.TotalMilliseconds }),
        });
        await RelayRuns.SaveAsync(db, cancellationToken);
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
        var mask = _board.Capabilities.Relay(relayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await EnsureNotInGroupAsync(mask, cancellationToken);
        var sent = _clock.UtcNow;
        await SendAsync(mask, on: false, cancellationToken);

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await RelayRuns.EndAsync(db, relayId, sent, cancellationToken);
        db.EventLog.Add(new EventLogEntry { TimestampUtc = now, BoardId = await db.FirstBoardIdAsync(cancellationToken), RelayId = relayId, Actor = actor, Action = EventActions.Off });
        await RelayRuns.SaveAsync(db, cancellationToken);
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
        var sent = _clock.UtcNow;
        var refused = ChannelMask.None;
        if (!single.IsEmpty)
        {
            _expected.Expect(wasOn, on: false);
            try
            {
                // Not taken: refused, or an answer the app couldn't read. The board's state, which already holds what
                // the command read, tells: one it shows off went off all the same.
                var taken = await _board.TurnOffAsync(single, cancellationToken);
                refused = single.Except(taken) & _board.State.Relays;
            }
            catch
            {
                // A failed or cancelled command logs nothing: whatever the board did shows as an external change.
                _expected.Forget(wasOn, on: false);
                throw;
            }

            // A relay the board refused (a board that switches a relay per request can refuse some) didn't go off: it
            // isn't logged, its run stays, and whatever it does later is its own change.
            // Only the expectations this command added: a relay that was off before isn't this command's.
            _expected.Forget(wasOn & refused, on: false);
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var relayId in await db.RelayRuntimes.Where(r => r.OnSinceUtc != null).Select(r => r.RelayId).ToListAsync(cancellationToken))
        {
            if (!refused.Contains(relayId))
            {
                await RelayRuns.EndAsync(db, relayId, sent, cancellationToken);
            }
        }

        var boardId = await db.FirstBoardIdAsync(cancellationToken);
        foreach (var relayId in wasOn.Except(refused).Channels)
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

        await RelayRuns.SaveAsync(db, cancellationToken);
        _changes.Notify();
        await _groups.StopAllAsync(userEmail, cancellationToken);
    }

    // Expects the change, then sends it. A command that fails or is cancelled logs nothing, so its expectation goes
    // too: whatever the board did then shows as an external change rather than vanishing, with an auto-off of its own.
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
