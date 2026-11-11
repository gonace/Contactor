using Microsoft.EntityFrameworkCore;
using Contactor.Vm201;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Groups;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// Turns relays on and off for a person: sends the command to the board first, and only once it went out records
/// who did it (<see cref="RelayRuntime"/>, auto-off time) and logs the event. If the board is not connected the
/// command fails with <see cref="Vm201NotConnectedException"/> and nothing is recorded. A relay in a group is only
/// turned on and off through its group (<see cref="GroupSequencer"/>), which keeps the group's order and interlock.
/// </summary>
public sealed class RelayControl
{
    private readonly Vm201Client _client;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly ExpectedChanges _expected;
    private readonly GroupSequencer _groups;

    public RelayControl(
        Vm201Client client,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        ExpectedChanges expected,
        GroupSequencer groups)
    {
        _client = client;
        _dbFactory = dbFactory;
        _clock = clock;
        _changes = changes;
        _expected = expected;
        _groups = groups;
    }

    public async Task TurnOnAsync(int relayId, string userEmail, CancellationToken cancellationToken)
    {
        var mask = Vm201Channel.Mask(relayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        await EnsureNotInGroupAsync(mask, cancellationToken);
        _expected.Expect(mask, on: true);
        await _client.TurnOnAsync(mask, cancellationToken);

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
        var mask = Vm201Channel.Mask(relayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        await EnsureNotInGroupAsync(mask, cancellationToken);
        _expected.Expect(mask, on: false);
        await _client.TurnOffAsync(mask, cancellationToken);

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        Clear(await GetRuntimeAsync(db, relayId, cancellationToken));
        db.EventLog.Add(new EventLogEntry { TimestampUtc = now, RelayId = relayId, Actor = userEmail, Action = EventActions.Off });
        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
    }

    /// <summary>
    /// "Alla av": turns every relay outside a group off with one command and logs one event for each that was on, then
    /// stops every group in its own order, with its run-on times.
    /// </summary>
    public async Task TurnAllOffAsync(string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (!_client.State.IsConnected)
        {
            throw new Vm201NotConnectedException();
        }

        var single = (byte)(Vm201Channel.All & ~await _groups.MemberMaskAsync(cancellationToken));
        var wasOn = (byte)(_client.State.Outputs & single);
        if (single != 0)
        {
            _expected.Expect(wasOn, on: false);
            await _client.TurnOffAsync(single, cancellationToken);
        }

        var now = _clock.UtcNow;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var runtime in await db.RelayRuntimes.ToListAsync(cancellationToken))
        {
            Clear(runtime);
        }

        for (var relayId = 1; relayId <= Vm201Channel.Count; relayId++)
        {
            if ((wasOn & Vm201Channel.Mask(relayId)) != 0)
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
        }

        await db.SaveChangesAsync(cancellationToken);
        _changes.Notify();
        await _groups.StopAllAsync(userEmail, cancellationToken);
    }

    private async Task EnsureNotInGroupAsync(byte mask, CancellationToken cancellationToken)
    {
        if ((await _groups.MemberMaskAsync(cancellationToken) & mask) != 0)
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
/// <see cref="Vm201Client.StateChanged"/>.
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
    public const string Text = "Reläet ingår i en grupp och styrs från gruppen.";

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
