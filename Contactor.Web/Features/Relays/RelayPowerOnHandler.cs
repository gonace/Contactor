using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Relays;

/// <summary>
/// Sets what a relay does by itself when its board gets power back (Issue #516), as a person saved it on the Relay
/// screen: at once through the board's <see cref="IRelayBoard.SetPowerOnAsync"/>, never on the app's own accord and never
/// queued, and logged with the person. The board is the source of truth: the app keeps no copy, and the board's next
/// state shows the new one (<see cref="BoardState.PowerOn"/>). A relay in a group gets its state on the group's page.
/// </summary>
/// <remarks>
/// A write the board didn't answer may still have been saved on the board. Such a write is kept as unconfirmed for
/// <see cref="ConfirmFor"/>: when the board's state shows it meanwhile, it is logged with the person then. It is never
/// sent again on its own; only a person's next Save sends anything.
/// </remarks>
public sealed partial class RelayPowerOnHandler : IDisposable
{
    /// <summary>How long a write the board didn't answer may still be confirmed by the board's state, and logged.</summary>
    public static readonly TimeSpan ConfirmFor = TimeSpan.FromMinutes(2);

    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly TimeProvider _time;
    private readonly RelayChanges _changes;
    private readonly IStringLocalizer<Texts> _texts;
    private readonly ILogger<RelayPowerOnHandler> _logger;

    // Writes the board didn't answer, by relay id: at most one per relay, the latest.
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Unconfirmed> _unconfirmed = [];

    public RelayPowerOnHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        TimeProvider time,
        RelayChanges changes,
        IStringLocalizer<Texts> texts,
        ILogger<RelayPowerOnHandler> logger)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _time = time;
        _changes = changes;
        _texts = texts;
        _logger = logger;
        _registry.StateChanged += OnBoardChanged;
        _registry.PowerOnChanged += OnBoardChanged;
    }

    public void Dispose()
    {
        _registry.StateChanged -= OnBoardChanged;
        _registry.PowerOnChanged -= OnBoardChanged;
    }

    /// <summary>
    /// Sets the relay's state after a power cut to <paramref name="state"/> at once, as <paramref name="userEmail"/>
    /// saved it, and logs it when the board took it. Returns null when set, otherwise the message to show: without a
    /// connection, or when the board refused it, nothing is logged or queued. When the board didn't answer, it says
    /// Contactor couldn't confirm it, and logs it later if the board's state shows it within <see cref="ConfirmFor"/>.
    /// </summary>
    public async Task<string?> SetAsync(int relayId, PowerOnState state, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state after a power cut.");
        }

        Relay? relay;
        bool inGroup;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            relay = await db.Relays.AsNoTracking().SingleOrDefaultAsync(r => r.Id == relayId, cancellationToken);
            inGroup = await db.RelayGroupMembers.AnyAsync(m => m.RelayId == relayId, cancellationToken);
        }

        if (relay is null || !_registry.HasRelay(relay.BoardId, relay.Channel))
        {
            return _texts["Relays.Missing"];
        }

        if (inGroup)
        {
            // Its group's page sets it, where the group's rule can be kept: nothing is sent.
            return _texts["Relays.PowerOn.InGroup"];
        }

        if (_registry.Find(relay.BoardId) is not { } board)
        {
            return _texts["Boards.NotChosen"];
        }

        // A new write takes the place of one the board didn't answer.
        lock (_lock)
        {
            _unconfirmed.Remove(relayId);
        }

        if (!board.State.IsConnected)
        {
            // Nothing is sent or queued.
            return _texts["Relays.PowerOn.NoContact"];
        }

        var from = board.State.PowerOn?.For(relay.Channel);
        var change = new Unconfirmed(relay.Id, relay.BoardId, relay.Channel, state, from, userEmail, _time.GetUtcNow() + ConfirmFor);
        try
        {
            await board.SetPowerOnAsync(relay.Channel, state, cancellationToken);
        }
        catch (BoardClosedException)
        {
            return _texts["Boards.BeingRemoved"];
        }
        catch (BoardNotConnectedException)
        {
            // No answer, the contact lost on the way, or the board saying it didn't take it: the board may have saved it
            // all the same, so the page doesn't claim either way. Its state tells.
            return await UnconfirmedAsync(change, board) ? null : _texts["Relays.PowerOn.Unconfirmed"].Value;
        }
        catch (BoardRefusedException)
        {
            return _texts["Relays.PowerOn.NotTaken"];
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentOutOfRangeException)
        {
            // A state or a relay the board doesn't have, e.g. after it got another model: nothing was sent.
            LogNotSent(ex, relay.BoardId);
            return _texts["Relays.PowerOn.NotTaken"];
        }

        // The board took it: logged also when the page closed meanwhile.
        await LogAsync(change);
        return null;
    }

    // Keeps a write the board didn't answer, to log once the board's state shows it. True when its state already does:
    // then it is logged now.
    private async Task<bool> UnconfirmedAsync(Unconfirmed change, LiveBoard board)
    {
        if (change.From == change.State)
        {
            // Nothing changes on the board either way, so there is nothing to log.
            return board.State.PowerOn?.For(change.Channel) == change.State;
        }

        lock (_lock)
        {
            _unconfirmed[change.RelayId] = change;
        }

        // The board's state may have shown it before the write returned: kept first, so the change is never missed.
        if (board.State.PowerOn?.For(change.Channel) == change.State && TryTake(change))
        {
            await LogAsync(change);
            return true;
        }

        return false;
    }

    // Takes the write out of those waiting, if it still is: only one caller logs it.
    private bool TryTake(Unconfirmed change)
    {
        lock (_lock)
        {
            return _unconfirmed.TryGetValue(change.RelayId, out var waiting) && ReferenceEquals(waiting, change) && _unconfirmed.Remove(change.RelayId);
        }
    }

    // A board's new state: the writes it now shows are logged; those past their time are dropped.
    private void OnBoardChanged(object? sender, BoardStateChange change)
    {
        List<Unconfirmed> confirmed = [];
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_unconfirmed.Count == 0)
            {
                return;
            }

            foreach (var waiting in _unconfirmed.Values.ToList())
            {
                if (waiting.Until <= now)
                {
                    _unconfirmed.Remove(waiting.RelayId);
                }
                else if (waiting.BoardId == change.BoardId && change.State.PowerOn?.For(waiting.Channel) == waiting.State)
                {
                    _unconfirmed.Remove(waiting.RelayId);
                    confirmed.Add(waiting);
                }
            }
        }

        foreach (var write in confirmed)
        {
            _ = LogAsync(write);
        }
    }

    // Logs the person's change the board took. A failed save is logged, not shown: the board has the new state, and the
    // page shows it.
    private async Task LogAsync(Unconfirmed change)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = _time.GetUtcNow().UtcDateTime,
                BoardId = change.BoardId,
                RelayId = change.RelayId,
                Actor = change.UserEmail,
                Action = EventActions.PowerOnSet,
                Details = change.From is { } before
                    ? JsonSerializer.Serialize(new { from = PowerOnStateNames.For(before), to = PowerOnStateNames.For(change.State) })
                    : JsonSerializer.Serialize(new { to = PowerOnStateNames.For(change.State) }),
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogNotLogged(ex, change.RelayId);
            return;
        }

        _changes.Notify();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The state after a power cut wasn't set on relay board {BoardId}: the board doesn't have it")]
    private partial void LogNotSent(Exception exception, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay {RelayId}'s new state after a power cut is on the board, but its event couldn't be saved")]
    private partial void LogNotLogged(Exception exception, int relayId);

    // A person's write: what it sets, what the board had, and until when the board's state may still confirm it.
    private sealed record Unconfirmed(int RelayId, int BoardId, int Channel, PowerOnState State, PowerOnState? From, string UserEmail, DateTimeOffset Until);
}
