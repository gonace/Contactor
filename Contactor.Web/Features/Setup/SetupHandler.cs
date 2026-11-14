using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Setup;

/// <summary>A relay as the setup guide shows it: its channel, its name, whether it is in use, and its group, if any.</summary>
public sealed record SetupRelay(int Id, int Channel, string Name, bool IsActive, string? GroupName)
{
    /// <summary>Whether it still has the name it was seeded with, so the guide shows that as a hint, not a value.</summary>
    public bool HasDefaultName => Name == Relay.DefaultName(Channel);
}

/// <summary>A relay's name and "In use" as typed in the setup guide.</summary>
public sealed record SetupRelayChoice(int Id, string? Name, bool IsActive);

/// <summary>
/// The setup guide a new installation opens on: whether it is done, the model of board, the relays' names and
/// "Try". Anyone signed in may go through it until someone finishes it; afterwards it isn't shown again.
/// </summary>
public sealed class SetupHandler
{
    /// <summary>How long "Try" keeps a relay on: long enough to hear which one it is.</summary>
    public static readonly TimeSpan TryLength = TimeSpan.FromSeconds(2);

    /// <summary>
    /// When auto-off ends a try that wasn't turned off: a little after <see cref="TryLength"/>, so it is only a
    /// backstop and never sends OFF at the same time as the turn-off.
    /// </summary>
    public static readonly TimeSpan TryAutoOffAfter = TryLength + TimeSpan.FromSeconds(5);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardChoice _choice;
    private readonly IRelayBoard _board;
    private readonly RelayControl _control;
    private readonly RelaySettingsHandler _relays;
    private readonly LocalClock _clock;
    private readonly TimeProvider _time;
    private readonly ILogger<SetupHandler> _logger;

    // Once done it stays done, so the relay list needn't ask the database on every visit.
    private volatile bool _done;

    public SetupHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardChoice choice,
        IRelayBoard board,
        RelayControl control,
        RelaySettingsHandler relays,
        LocalClock clock,
        TimeProvider time,
        ILogger<SetupHandler> logger)
    {
        _dbFactory = dbFactory;
        _choice = choice;
        _board = board;
        _control = control;
        _relays = relays;
        _clock = clock;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether the guide has been finished: until then, every page opens on it.</summary>
    public async Task<bool> IsDoneAsync(CancellationToken cancellationToken)
    {
        if (_done)
        {
            return true;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        _done = await db.Installations.AnyAsync(i => i.SetupDoneUtc != null, cancellationToken);
        return _done;
    }

    /// <summary>Records that the guide was finished; the app opens on the relay list from then on.</summary>
    public async Task MarkDoneAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var installation = await GetAsync(db, cancellationToken);
        installation.SetupDoneUtc ??= _clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        _done = true;
    }

    /// <summary>
    /// Saves the model of board and switches to it at once, while the app runs (<see cref="BoardChoice"/>): the guide
    /// goes straight on. Throws <see cref="InvalidOperationException"/> once the guide is done: the model is never
    /// changed afterwards (replacing the board is issue #32).
    /// </summary>
    public async Task ChooseBoardAsync(string type, CancellationToken cancellationToken)
    {
        var model = BoardCatalogue.Find(type)?.Model
            ?? throw new ArgumentException($"\"{type}\" isn't a board in the catalogue.", nameof(type));
        if (await IsDoneAsync(cancellationToken))
        {
            throw new InvalidOperationException("The setup guide is done; the board's model is no longer chosen there.");
        }

        await _choice.ChooseAsync(model, cancellationToken);
    }

    /// <summary>Every relay the board has, in order, with its name and group.</summary>
    public async Task<IReadOnlyList<SetupRelay>> RelaysAsync(CancellationToken cancellationToken)
    {
        var count = _board.Capabilities.RelayCount;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.RelayGroupMembers.AsNoTracking()
            .Select(m => new { m.RelayId, m.Group!.Name })
            .ToDictionaryAsync(m => m.RelayId, m => m.Name, cancellationToken);
        var relays = await db.Relays.AsNoTracking()
            .Where(r => r.Channel <= count)
            .OrderBy(r => r.Channel)
            .ToListAsync(cancellationToken);
        return [.. relays.Select(r => new SetupRelay(r.Id, r.Channel, r.Name, r.IsActive, groups.GetValueOrDefault(r.Id)))];
    }

    /// <summary>
    /// Saves the relays' names and "In use", logged as on the Relay screen. An empty name keeps the relay's default
    /// ("Relay 3"). Returns null when saved, otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveRelaysAsync(IEnumerable<SetupRelayChoice> choices, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var channels = await BoardChannels.LoadAsync(_dbFactory, cancellationToken);
        foreach (var choice in choices)
        {
            if (await _relays.GetAsync(choice.Id, cancellationToken) is not { } current || channels.RelayChannel(choice.Id) is not { } channel)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(choice.Name) ? Relay.DefaultName(channel) : choice.Name;
            if (await _relays.SaveAsync(current with { Name = name, IsActive = choice.IsActive }, userEmail, cancellationToken) is { } error)
            {
                return $"Relay {channel}: {error}";
            }
        }

        return null;
    }

    /// <summary>
    /// "Try": turns the relay on for <see cref="TryLength"/>, so the person can hear which one it is, through
    /// <see cref="RelayControl"/> (so a relay in a group is refused). On a board with a pulse that can do it, the board
    /// turns the relay off again by itself, also when the app stops meanwhile, and it is logged as one event.
    /// Otherwise, as below: the app turns it on and off, both steps logged. Returns once it is
    /// on; it is turned off again even if the page is closed. Its auto-off is set a little later, so
    /// <see cref="AutoOffService"/> turns it off should the app stop or the board drop before that. Throws
    /// <see cref="BoardNotConnectedException"/> without a board, and <see cref="RelayInGroupException"/> for a relay in
    /// a group. An ON that fails may still have switched the relay on: if the board shows it on at the end of the try, it
    /// is turned off too.
    /// </summary>
    public async Task TryAsync(int relayId, string userEmail, CancellationToken cancellationToken)
    {
        if (_board.Capabilities.Pulse?.CanDo(TryLength) == true && TryLength <= RelayControl.MaxPulse)
        {
            await _control.PulseAsync(relayId, TryLength, userEmail, cancellationToken);
            return;
        }

        try
        {
            await _control.TurnOnAsync(relayId, userEmail, cancellationToken);
        }
        catch (Exception ex) when (ex is not RelayInGroupException and not OperationCanceledException and not ArgumentException)
        {
            // Refused, unreadable, no answer, or a failure after ON went out: the board may have switched it on all the
            // same, and as a relay on outside the app it would get the relay's full auto-off, not the try's moment.
            _ = TurnOffIfOnAsync(relayId, userEmail);
            throw;
        }

        // ON has gone out: the turn-off starts now, whatever happens to the save below.
        _ = TurnOffLaterAsync(relayId, userEmail);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var runtime = await db.RelayRuntimes.AsNoTracking().SingleAsync(r => r.RelayId == relayId, cancellationToken);
        if (runtime.OnSinceUtc is { } since && runtime.TurnedOnBy == userEmail)
        {
            await RelayRuns.SetAutoOffAsync(db, relayId, since, _clock.UtcNow + TryAutoOffAfter, cancellationToken);
            await RelayRuns.SaveAsync(db, cancellationToken);
        }
    }

    // After an ON that failed: the try promised only a moment, so a relay the board shows on by its end is turned off.
    // A command that never reached the board leaves no OFF and no event.
    private async Task TurnOffIfOnAsync(int relayId, string userEmail)
    {
        try
        {
            await Task.Delay(TryLength, _time);
            var channels = await BoardChannels.LoadAsync(_dbFactory, CancellationToken.None);
            if (channels.RelayChannel(relayId) is not { } channel || !_board.State.IsOn(channel))
            {
                _logger.TryNotOn(relayId);
                return;
            }

            await _control.TurnOffAsync(relayId, userEmail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Not switched off here: its own auto-off, as a relay on outside the app, ends it.
            _logger.TryNotEnded(ex, relayId);
        }
    }

    private async Task TurnOffLaterAsync(int relayId, string userEmail)
    {
        try
        {
            await Task.Delay(TryLength, _time);
            await _control.TurnOffAsync(relayId, userEmail, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Not switched off here: its auto-off, set a little later, ends it at the next check.
            _logger.TryNotEnded(ex, relayId);
        }
    }

    private static async Task<Installation> GetAsync(ContactorDbContext db, CancellationToken cancellationToken)
    {
        var installation = await db.Installations.SingleOrDefaultAsync(cancellationToken);
        if (installation is null)
        {
            installation = new Installation();
            db.Installations.Add(installation);
        }

        return installation;
    }
}

internal static partial class SetupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "\"Try\" of relay {RelayId} failed and the relay didn't come on: nothing to turn off")]
    public static partial void TryNotOn(this ILogger logger, int relayId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "\"Try\" couldn't turn relay {RelayId} off again")]
    public static partial void TryNotEnded(this ILogger logger, Exception exception, int relayId);
}
