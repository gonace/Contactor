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
public sealed partial class RelayPowerOnHandler
{
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly BoardRegistry _registry;
    private readonly LocalClock _clock;
    private readonly RelayChanges _changes;
    private readonly IStringLocalizer<Texts> _texts;
    private readonly ILogger<RelayPowerOnHandler> _logger;

    public RelayPowerOnHandler(
        IDbContextFactory<ContactorDbContext> dbFactory,
        BoardRegistry registry,
        LocalClock clock,
        RelayChanges changes,
        IStringLocalizer<Texts> texts,
        ILogger<RelayPowerOnHandler> logger)
    {
        _dbFactory = dbFactory;
        _registry = registry;
        _clock = clock;
        _changes = changes;
        _texts = texts;
        _logger = logger;
    }

    /// <summary>
    /// Sets the relay's state after a power cut to <paramref name="state"/> at once, as <paramref name="userEmail"/>
    /// saved it, and logs it when the board took it. Returns null when set, otherwise the message to show: without a
    /// connection, or when the board didn't take it, nothing is logged or queued.
    /// </summary>
    public async Task<string?> SetAsync(int relayId, PowerOnState state, string userEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userEmail);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Not a state after a power cut.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var relay = await db.Relays.AsNoTracking().SingleOrDefaultAsync(r => r.Id == relayId, cancellationToken);
        if (relay is null || !_registry.HasRelay(relay.BoardId, relay.Channel))
        {
            return _texts["Relays.Missing"];
        }

        if (await db.RelayGroupMembers.AnyAsync(m => m.RelayId == relayId, cancellationToken))
        {
            // Its group's page sets it, where the group's rule can be kept: nothing is sent.
            return _texts["Relays.PowerOn.InGroup"];
        }

        if (_registry.Find(relay.BoardId) is not { } board)
        {
            return _texts["Boards.NotChosen"];
        }

        var from = board.State.PowerOn?.For(relay.Channel);
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
            // Also how a board says it didn't take it, while it answers.
            return board.State.IsConnected ? _texts["Relays.PowerOn.NotTaken"] : _texts["Relays.PowerOn.NoContact"];
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
        db.EventLog.Add(new EventLogEntry
        {
            TimestampUtc = _clock.UtcNow,
            BoardId = relay.BoardId,
            RelayId = relay.Id,
            Actor = userEmail,
            Action = EventActions.PowerOnSet,
            Details = from is { } before
                ? JsonSerializer.Serialize(new { from = PowerOnStateNames.For(before), to = PowerOnStateNames.For(state) })
                : JsonSerializer.Serialize(new { to = PowerOnStateNames.For(state) }),
        });
        await db.SaveChangesAsync(CancellationToken.None);
        _changes.Notify();
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The state after a power cut wasn't set on relay board {BoardId}: the board doesn't have it")]
    private partial void LogNotSent(Exception exception, int boardId);
}
