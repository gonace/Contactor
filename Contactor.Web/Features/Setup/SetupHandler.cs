using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Setup;

/// <summary>A relay as the setup guide shows it: its name, whether it is in use, and its group, if any.</summary>
public sealed record SetupRelay(int Id, string Name, bool IsActive, string? GroupName)
{
    /// <summary>Whether it still has the name it was seeded with, so the guide shows that as a hint, not a value.</summary>
    public bool HasDefaultName => Name == Relay.DefaultName(Id);
}

/// <summary>A relay's name and "In use" as typed in the setup guide.</summary>
public sealed record SetupRelayChoice(int Id, string? Name, bool IsActive);

/// <summary>
/// The setup guide a new installation opens on: whether it is done, the model of board, the relays' names and
/// "Try". Anyone signed in may go through it until someone finishes or skips it; afterwards it isn't shown again.
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

    // After a failed ON: how often the relay is looked at, and how long past the credit's lifetime.
    private static readonly TimeSpan CreditCheckEvery = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CreditGrace = TimeSpan.FromSeconds(1);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardModel _model;
    private readonly IRelayBoard _board;
    private readonly RelayControl _control;
    private readonly RelaySettingsHandler _relays;
    private readonly ExpectedChanges _expected;
    private readonly LocalClock _clock;
    private readonly TimeProvider _time;
    private readonly ILogger<SetupHandler> _logger;

    // Once done it stays done, so the relay list needn't ask the database on every visit.
    private volatile bool _done;

    public SetupHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardModel model,
        IRelayBoard board,
        RelayControl control,
        RelaySettingsHandler relays,
        ExpectedChanges expected,
        LocalClock clock,
        TimeProvider time,
        ILogger<SetupHandler> logger)
    {
        _dbFactory = dbFactory;
        _model = model;
        _board = board;
        _control = control;
        _relays = relays;
        _expected = expected;
        _clock = clock;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether the guide has been finished or skipped.</summary>
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

    /// <summary>Records that the guide was finished or skipped; the app opens on the relay list from then on.</summary>
    public async Task MarkDoneAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var installation = await GetAsync(db, cancellationToken);
        installation.SetupDoneUtc ??= _clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        _done = true;
    }

    /// <summary>
    /// Saves the model of board, which wins over <c>Board:Type</c> from now on. Returns true when it isn't the model
    /// the app runs with, so the app has to restart to use it. Throws <see cref="InvalidOperationException"/> once the
    /// guide is done: the model is never changed afterwards (replacing the board is issue #32).
    /// </summary>
    public async Task<bool> ChooseBoardAsync(string type, CancellationToken cancellationToken)
    {
        var model = BoardCatalogue.Find(type)?.Model
            ?? throw new ArgumentException($"\"{type}\" isn't a board in the catalogue.", nameof(type));
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var installation = await GetAsync(db, cancellationToken);
        if (installation.SetupDoneUtc is not null)
        {
            throw new InvalidOperationException("The setup guide is done; the board's model is no longer chosen there.");
        }

        installation.BoardType = model.Id;
        await db.SaveChangesAsync(cancellationToken);
        var restart = model.Id != _model.Id;
        if (restart)
        {
            _logger.BoardChosen(model.Name, _model.Name);
        }

        return restart;
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
            .Where(r => r.Id <= count)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);
        return [.. relays.Select(r => new SetupRelay(r.Id, r.Name, r.IsActive, groups.GetValueOrDefault(r.Id)))];
    }

    /// <summary>
    /// Saves the relays' names and "In use", logged as on the Relay screen. An empty name keeps the relay's default
    /// ("Relay 3"). Returns null when saved, otherwise the message to show.
    /// </summary>
    public async Task<string?> SaveRelaysAsync(IEnumerable<SetupRelayChoice> choices, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(choices);
        foreach (var choice in choices)
        {
            if (await _relays.GetAsync(choice.Id, cancellationToken) is not { } current)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(choice.Name) ? Relay.DefaultName(choice.Id) : choice.Name;
            if (await _relays.SaveAsync(current with { Name = name, IsActive = choice.IsActive }, userEmail, cancellationToken) is { } error)
            {
                return $"Relay {choice.Id}: {error}";
            }
        }

        return null;
    }

    /// <summary>
    /// "Try": turns the relay on for <see cref="TryLength"/>, so the person can hear which one it is, through
    /// <see cref="RelayControl"/> (so a relay in a group is refused, and both steps are logged). Returns once it is
    /// on; it is turned off again even if the page is closed. Its auto-off is set a little later, so
    /// <see cref="AutoOffService"/> turns it off should the app stop or the board drop before that. Throws
    /// <see cref="BoardNotConnectedException"/> without a board, and <see cref="RelayInGroupException"/> for a relay in
    /// a group. An ON that fails may still have switched the relay on: if the board shows it on while a failed ON can
    /// still be credited to the person (<see cref="ExpectedChanges.Lifetime"/>), it is turned off too.
    /// </summary>
    public async Task TryAsync(int relayId, string userEmail, CancellationToken cancellationToken)
    {
        // Before the command: a failed ON is remembered, and can be credited, before its exception gets back here.
        var triedUtc = _clock.UtcNow;
        try
        {
            await _control.TurnOnAsync(relayId, userEmail, cancellationToken);
        }
        catch (Exception ex) when (ex is not RelayInGroupException and not OperationCanceledException and not ArgumentException)
        {
            // Refused, unreadable, no answer, or a failure after ON went out: the board may have switched it on all the
            // same, and its credited ON gets the relay's full auto-off, not the try's few seconds.
            _ = TurnOffIfCreditedAsync(relayId, userEmail, triedUtc);
            throw;
        }

        // ON has gone out: the turn-off starts now, whatever happens to the save below.
        _ = TurnOffLaterAsync(relayId, userEmail);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var runtime = await db.RelayRuntimes.SingleAsync(r => r.RelayId == relayId, cancellationToken);
        runtime.AutoOffAtUtc = _clock.UtcNow + TryAutoOffAfter;
        await db.SaveChangesAsync(cancellationToken);
    }

    // After an ON that failed: the relay is turned off only once the board shows it on and the ON was credited to this
    // person (BoardEventRecorder), so a command that never reached the board leaves no OFF and no event, and a relay that
    // came on for another reason (a schedule, an input, someone else) is left alone. Watched for as long as the failed ON
    // can be credited (ExpectedChanges.HasFailedOn), at least its first few seconds and at most FailedOnLimit, plus a
    // little for the recorder to save the credit.
    private async Task TurnOffIfCreditedAsync(int relayId, string userEmail, DateTime failedUtc)
    {
        try
        {
            await Task.Delay(TryLength, _time);

            // How long the failed ON has no longer been creditable: a credit just taken may still be on its way to the
            // database, so that lasts a little before the watch ends.
            var settled = TimeSpan.Zero;
            for (var waited = TryLength; ; waited += CreditCheckEvery)
            {
                settled = _expected.HasFailedOn(relayId, userEmail) ? TimeSpan.Zero : settled + CreditCheckEvery;
                if (_board.State.IsOn(relayId) && await IsCreditedAsync(relayId, userEmail, failedUtc))
                {
                    await _control.TurnOffAsync(relayId, userEmail, CancellationToken.None);
                    return;
                }

                if ((settled > CreditGrace && waited >= ExpectedChanges.Lifetime + CreditGrace) || waited >= ExpectedChanges.FailedOnLimit + CreditGrace)
                {
                    _logger.TryNotOn(relayId);
                    return;
                }

                await Task.Delay(CreditCheckEvery, _time);
            }
        }
        catch (Exception ex)
        {
            // Not switched off here: its credited auto-off ends it.
            _logger.TryNotEnded(ex, relayId);
        }
    }

    private async Task<bool> IsCreditedAsync(int relayId, string userEmail, DateTime failedUtc)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.RelayRuntimes.AnyAsync(r => r.RelayId == relayId && r.TurnedOnBy == userEmail && r.OnSinceUtc >= failedUtc);
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
    [LoggerMessage(Level = LogLevel.Information, Message = "The setup guide chose the {Chosen}; the app runs with the {Running} and restarts to use it")]
    public static partial void BoardChosen(this ILogger logger, string chosen, string running);

    [LoggerMessage(Level = LogLevel.Information, Message = "\"Try\" of relay {RelayId} failed and the relay didn't come on for it: nothing to turn off")]
    public static partial void TryNotOn(this ILogger logger, int relayId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "\"Try\" couldn't turn relay {RelayId} off again")]
    public static partial void TryNotEnded(this ILogger logger, Exception exception, int relayId);
}
