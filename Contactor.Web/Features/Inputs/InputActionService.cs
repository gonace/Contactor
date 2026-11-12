using System.Threading.Channels;
using Contactor.Boards;
using Contactor.Web.Features.Groups;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Inputs;

/// <summary>An input's change as the app saw it happen, for <see cref="InputActionService"/>.</summary>
public sealed record InputChanged(int InputId, bool Closed);

/// <summary>
/// The inputs' changes the app saw as they happened, handed over by <see cref="Events.BoardEventRecorder"/> once they
/// are logged. A change only noticed after a gap in contact is never queued: nobody knows when it happened.
/// </summary>
public sealed class InputChangeFeed
{
    private readonly Channel<InputChanged> _changes = Channel.CreateUnbounded<InputChanged>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<InputChanged> Reader => _changes.Reader;

    public void Enqueue(int inputId, bool closed)
    {
        _changes.Writer.TryWrite(new InputChanged(inputId, closed));
    }
}

/// <summary>
/// Carries out an input's actions when it closes or opens, as if someone pressed the buttons at that moment: a relay
/// through <see cref="RelayControl"/> (with its auto-off), a group through <see cref="GroupSequencer"/> (in its order,
/// with its run-on and auto-off). Logged with the input as the actor. Every relay command is sent, as a press would be:
/// the board's state may not show a command that is still on its way. A group decides itself whether there is
/// anything to stop; one that is fully on and not mid-sequence isn't started again. Without the board nothing is sent
/// or queued for later; a relay that has joined a group is skipped.
/// </summary>
public sealed partial class InputActionService : BackgroundService
{
    private readonly InputChangeFeed _queue;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly IRelayBoard _board;
    private readonly RelayControl _relays;
    private readonly GroupSequencer _groups;
    private readonly ILogger<InputActionService> _logger;

    public InputActionService(
        InputChangeFeed queue,
        IDbContextFactory<ContactorDbContext> dbFactory,
        IRelayBoard board,
        RelayControl relays,
        GroupSequencer groups,
        ILogger<InputActionService> logger)
    {
        _queue = queue;
        _dbFactory = dbFactory;
        _board = board;
        _relays = relays;
        _groups = groups;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await RunAsync(change, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>Carries out what the input does on this change, one relay or group at a time.</summary>
    public async Task RunAsync(InputChanged change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        List<InputAction> actions;
        Dictionary<int, List<int>> members;
        Dictionary<int, GroupRuntime> runtimes;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            actions = await db.InputActions.AsNoTracking()
                .Where(a => a.InputId == change.InputId)
                .OrderBy(a => a.GroupId == null).ThenBy(a => a.GroupId).ThenBy(a => a.RelayId)
                .ToListAsync(cancellationToken);
            members = (await db.RelayGroupMembers.AsNoTracking().Select(m => new { m.GroupId, m.RelayId }).ToListAsync(cancellationToken))
                .GroupBy(m => m.GroupId)
                .ToDictionary(g => g.Key, g => g.Select(m => m.RelayId).ToList());
            runtimes = await db.GroupRuntimes.AsNoTracking().ToDictionaryAsync(r => r.GroupId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex, change.InputId);
            return;
        }

        var actor = EventActors.ForInput(change.InputId);
        foreach (var action in actions)
        {
            if ((change.Closed ? action.WhenClosed : action.WhenOpened) is not { } on)
            {
                continue;
            }

            try
            {
                await RunAsync(action, on, actor, members, runtimes, cancellationToken);
            }
            catch (BoardNotConnectedException)
            {
                // Never queued for later: the input's moment has passed.
                LogNoBoard(change.InputId);
                return;
            }
            catch (RelayInGroupException)
            {
                LogRelayInGroup(change.InputId, action.RelayId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One relay or group failing doesn't stop the rest.
                LogFailed(ex, change.InputId);
            }
        }
    }

    private async Task RunAsync(
        InputAction action,
        bool on,
        string actor,
        Dictionary<int, List<int>> members,
        Dictionary<int, GroupRuntime> runtimes,
        CancellationToken cancellationToken)
    {
        if (action.GroupId is { } groupId)
        {
            var groupRelays = members.GetValueOrDefault(groupId) ?? [];
            if (groupRelays.Count == 0)
            {
                return;
            }

            if (!on)
            {
                // The sequencer knows best what is running or on its way (an ON not confirmed yet is overtaken).
                await _groups.StopGroupAsync(groupId, actor, cancellationToken);
            }
            else if (!IsFullyOn(groupRelays, runtimes.GetValueOrDefault(groupId)))
            {
                await _groups.StartGroupAsync(groupId, actor, cancellationToken);
            }
        }
        else if (action.RelayId is { } relayId && relayId <= _board.Capabilities.RelayCount)
        {
            if (on)
            {
                await _relays.TurnOnAsync(relayId, actor, cancellationToken);
            }
            else
            {
                await _relays.TurnOffAsync(relayId, actor, cancellationToken);
            }
        }
    }

    // Every relay on, nothing on its way and no sequence under way: starting it again would only log a second start.
    private bool IsFullyOn(List<int> groupRelays, GroupRuntime? runtime)
    {
        return groupRelays.All(_board.State.Relays.Contains)
            && (runtime is null || (runtime.Phase == GroupPhases.Idle && runtime.PendingPosition is null));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Input {InputId} changed, but the board isn't connected: its actions were not carried out")]
    private partial void LogNoBoard(int inputId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Input {InputId} didn't switch relay {RelayId}: it is in a group now")]
    private partial void LogRelayInGroup(int inputId, int? relayId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Input {InputId}'s actions failed")]
    private partial void LogFailed(Exception exception, int inputId);
}
