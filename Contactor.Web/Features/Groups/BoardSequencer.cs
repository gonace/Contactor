using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Groups;

/// <summary>
/// Runs one board's relay groups (<see cref="GroupPlanner"/> has the rules): on every status from that board and every
/// second it plans each of its groups' next step, sends the commands to that board, and saves the groups'
/// <see cref="GroupRuntime"/> and the events. Requests from people (start, stop, one relay on or off) go through the same
/// lock, so one thing happens at a time on the board. While the board is disconnected nothing happens; run-on times keep
/// counting, and on reconnect the interlock is checked first, then whatever fell due meanwhile. Another board's groups,
/// lost or not, never hold it up. Made by <see cref="GroupSequencer"/> for each live board, subscribed before its
/// connection starts, and ended with it.
/// </summary>
public sealed partial class BoardSequencer : IBoardPart, IAsyncDisposable
{
    private readonly LiveBoard _board;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly ExpectedChanges _expected;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BoardSequencer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _ended = new();
    private bool _wasConnected;
    private int _reconnected;
    private int _disposed;

    // Groups with a relay the board doesn't have whose shut-off is saved in the event log and not yet confirmed, as of
    // the last step that saved; rebuilt by each one. Under _gate.
    private readonly HashSet<int> _shutOffLogged = [];

    internal BoardSequencer(
        LiveBoard board,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILogger<BoardSequencer> logger)
    {
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _expected = board.Expected;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;

        // From its making, before the board's connection starts: no status may go unseen.
        _board.StateChanged += OnStateChanged;
    }

    /// <inheritdoc/>
    public int BoardId => _board.BoardId;

    /// <summary>The board it runs the groups of.</summary>
    public LiveBoard Board => _board;

    /// <summary>
    /// Stops listening and ends the loop. A step or request under way finishes first, so nothing is left half sent and
    /// unsaved; one that comes after throws <see cref="BoardNotConnectedException"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _board.StateChanged -= OnStateChanged;
        await _ended.CancelAsync();
        await _gate.WaitAsync(CancellationToken.None);
        _gate.Release();

        // The semaphores and the token source are left to the collector: a request may still be waiting on the gate.
    }

    // Only once the step's events are saved, so a shut-off whose OFF or save failed is logged by the next step.
    private void RememberShutOffs(HashSet<int> shutOff)
    {
        _shutOffLogged.Clear();
        _shutOffLogged.UnionWith(shutOff);
    }

    /// <summary>
    /// Whether every relay of the group is on this board, by its channel; one left from a board with more relays isn't,
    /// nor one on another board (a group's relays are all on one board). The members come with their relays.
    /// </summary>
    private bool IsOnBoard(RelayGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Members.All(IsOnBoard);
    }

    private bool IsOnBoard(RelayGroupMember member)
    {
        return member.Relay!.BoardId == BoardId && Channel(member) <= _board.Capabilities.RelayCount;
    }

    // A member's channel on the board; the members are read with their relays.
    private static int Channel(RelayGroupMember member)
    {
        return member.Relay?.Channel ?? throw new InvalidOperationException("A group's members are read with their relays.");
    }

    /// <summary>The channels of this board's relays that are in a group.</summary>
    public async Task<ChannelMask> MemberMaskAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return ChannelMask.Of(await db.RelayGroupMembers
            .Where(m => m.Relay!.BoardId == BoardId)
            .Select(m => m.Relay!.Channel)
            .ToListAsync(cancellationToken));
    }

    /// <summary>Plans and runs the next step of every group of the board. Does nothing while the board is disconnected.</summary>
    public async Task StepAsync(CancellationToken cancellationToken)
    {
        await RunAsync(null, cancellationToken);
    }

    /// <summary>Starts a group, or carries on from where it is. Throws <see cref="BoardNotConnectedException"/> without the board.</summary>
    public async Task StartGroupAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await RunAsync(
            (group, members, state, runtime, now, commands) =>
            {
                if (group.Id == groupId)
                {
                    // A schedule's OffTime is the end of the run it starts; it gets no auto-off.
                    var maxOnMinutes = actor == EventActors.Schedule ? null : group.MaxOnMinutes;
                    GroupPlanner.Start(members, state, runtime, actor, maxOnMinutes, now, commands);
                }

                return null;
            },
            cancellationToken);
    }

    /// <summary>Stops a group, with its run-on times. Throws <see cref="BoardNotConnectedException"/> without the board.</summary>
    public async Task StopGroupAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await RunAsync(
            (group, members, state, runtime, now, commands) =>
            {
                if (group.Id == groupId)
                {
                    GroupPlanner.Stop(members, state, runtime, actor, now, commands);
                }

                return null;
            },
            cancellationToken);
    }

    /// <summary>Stops every group of the board, with run-on times ("All off").</summary>
    public async Task StopAllAsync(string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await RunAsync(
            (group, members, state, runtime, now, commands) =>
            {
                GroupPlanner.Stop(members, state, runtime, actor, now, commands);
                return null;
            },
            cancellationToken);
    }

    /// <summary>
    /// Turns one relay of a group on or off, within the interlock. Returns why it can't, or null when
    /// done. Throws <see cref="BoardNotConnectedException"/> without the board.
    /// </summary>
    public async Task<string?> SetMemberAsync(int groupId, int relayId, bool on, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        string? refusal = "That relay isn't in the group.";
        await RunAsync(
            (group, members, state, runtime, now, commands) =>
            {
                if (group.Id != groupId)
                {
                    return null;
                }

                var member = members.FirstOrDefault(m => m.RelayId == relayId);
                if (member is null)
                {
                    return null;
                }

                refusal = null;
                if (on)
                {
                    refusal = GroupPlanner.MemberOn(members, state, runtime, member.Position, actor, group.MaxOnMinutes, now, commands);
                }
                else
                {
                    GroupPlanner.MemberOff(members, state, runtime, member.Position, actor, now, commands);
                }

                return refusal;
            },
            cancellationToken);
        return refusal;
    }

    /// <summary>The board's loop: a step on every status and every second, until cancelled or disposed.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ended.Token);
        var stoppingToken = linked.Token;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (BoardNotConnectedException)
            {
                // The board went away mid-step: the next step after reconnect picks up.
            }
#pragma warning disable CA1031 // One failed step must not stop the groups for good; it is logged and retried.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogStepFailed(ex, BoardId);
            }

            await WaitAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // Until a status arrives or a second has passed. The waiter that loses is cancelled, so none piles up.
    private async Task WaitAsync(CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var woken = _wake.WaitAsync(cts.Token);
        var delay = Task.Delay(GroupSequencer.Interval, _timeProvider, cts.Token);
        await Task.WhenAny(woken, delay);
        await cts.CancelAsync();
        try
        {
            await woken;
        }
        catch (OperationCanceledException)
        {
            // The second passed first.
        }

        try
        {
            await delay;
        }
        catch (OperationCanceledException)
        {
            // A status came first.
        }
    }

    // One step for every group, with an optional request applied to the groups first. A request's refusal (a string)
    // means nothing is sent or saved for it.
    private async Task RunAsync(GroupRequest? request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = _board.State;
            if (!state.IsConnected || Volatile.Read(ref _disposed) == 1)
            {
                if (request is not null)
                {
                    throw new BoardNotConnectedException();
                }

                return;
            }

            var reconnected = Interlocked.Exchange(ref _reconnected, 0) == 1;
            var now = _clock.UtcNow;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var groups = await db.RelayGroups.Include(g => g.Members).ThenInclude(m => m.Relay)
                .Where(g => g.BoardId == BoardId)
                .OrderBy(g => g.Id)
                .ToListAsync(cancellationToken);
            if (groups.Count == 0)
            {
                _shutOffLogged.Clear();
                return;
            }

            var runtimes = await db.GroupRuntimes.ToDictionaryAsync(r => r.GroupId, cancellationToken);
            var all = new GroupCommands();
            List<(int GroupId, GroupEvent Event)> events = [];
            HashSet<int> shutOff = [];
            foreach (var group in groups)
            {
                if (!runtimes.TryGetValue(group.Id, out var runtime))
                {
                    runtime = new GroupRuntime { GroupId = group.Id };
                    db.GroupRuntimes.Add(runtime);
                }

                // A group with a relay the board doesn't have (left from a board with more relays, or one on another
                // board) can't run its order or its interlock, so its relays on this board are kept off (fail closed)
                // until it is changed or deleted. Only this board's: a channel here means a relay on this board.
                if (!IsOnBoard(group))
                {
                    var on = group.Members.Where(m => m.Relay!.BoardId == BoardId && state.IsOn(Channel(m))).OrderBy(m => m.Position).ToList();
                    all.Off |= ChannelMask.Of(on.Select(Channel));
                    runtime.Phase = GroupPhases.Idle;
                    runtime.TargetLevel = 0;
                    runtime.NextStepAtUtc = null;
                    runtime.PendingPosition = null;
                    runtime.PendingOn = false;
                    runtime.PendingSinceUtc = null;
                    runtime.OnSinceUtc = null;
                    runtime.AutoOffAtUtc = null;
                    runtime.TurnedOnBy = null;
                    if (on.Count > 0 && shutOff.Add(group.Id) && !_shutOffLogged.Contains(group.Id))
                    {
                        // Sent again on every step until the board confirms it, but logged once.
                        // "Fan turned off because Heat was off": the relay the board doesn't have can't be on.
                        var missing = group.Members.Where(m => !IsOnBoard(m)).OrderBy(m => m.Position).First();
                        var details = JsonSerializer.Serialize(new { missing = missing.RoleName, off = on.Select(m => m.RoleName) });
                        events.Add((group.Id, new GroupEvent(EventActions.InterlockTrip, missing.RelayId, EventActors.System, details)));
                    }

                    continue;
                }

                var members = group.Members
                    .OrderBy(m => m.Position)
                    .Select(m => new GroupMemberInfo(m.Position, m.RelayId, Channel(m), m.RoleName, m.StartDelaySec, m.RunOnSec))
                    .ToList();
                if (reconnected && runtime.PendingPosition is { } pending)
                {
                    // A command from before the connection dropped (or the app restarted) was done or lost. The board
                    // gets a fresh timeout to show it; a confirmation then times the next step as usual, so no
                    // start delay or run-on is skipped. The relay a stop (a trip) needs for its run-on, e.g. the fan
                    // after the heat, is sent once more in case its ON was lost: turning on a relay above never breaks
                    // the interlock.
                    runtime.PendingSinceUtc = now;
                    if (runtime.PendingOn && runtime.Phase == GroupPhases.Stopping && members.FirstOrDefault(m => m.Position == pending) is { } lost)
                    {
                        all.On |= ChannelMask.Of(lost.Channel);
                    }
                }

                var commands = new GroupCommands();
                if (request is not null && request(group, members, state, runtime, now, commands) is not null && commands.IsEmpty)
                {
                    // Refused: nothing for this group this time.
                    continue;
                }

                if (commands.IsEmpty)
                {
                    GroupPlanner.Plan(members, state, runtime, now, commands);
                }

                GroupPlanner.GiveOwnerlessAutoOff(members, state, runtime, group.MaxOnMinutes, now);

                all.Off |= commands.Off;
                all.On |= commands.On;
                events.AddRange(commands.Events.Select(e => (group.Id, e)));
            }

            // OFF first, so a relay never runs alone below a gap. Nothing sent: nothing is saved, and the next step
            // plans again. Once anything went out, what was planned is saved even if the rest fails, so a trip or a
            // run-on is never forgotten.
            if (!all.Off.IsEmpty)
            {
                _expected.Expect(all.Off, on: false);
                try
                {
                    // A relay the board refused (one that switches a relay per request can refuse some) isn't the
                    // app's change: its step isn't confirmed, and times out as one the board didn't carry out. One whose
                    // answer couldn't be read, but which the board's state (it holds what the command read) shows off,
                    // went off all the same.
                    var taken = await _board.TurnOffAsync(all.Off, cancellationToken);
                    _expected.Forget(all.Off.Except(taken) & _board.State.Relays, on: false);
                }
                catch
                {
                    // A failed or cancelled command's change isn't the app's: whatever the board did shows as an
                    // external one.
                    _expected.Forget(all.Off, on: false);
                    throw;
                }
            }

            if (!all.On.IsEmpty)
            {
                _expected.Expect(all.On, on: true);
                try
                {
                    var taken = await _board.TurnOnAsync(all.On, cancellationToken);
                    _expected.Forget(all.On.Except(taken).Except(_board.State.Relays), on: true);
                }
                catch (Exception ex)
                {
                    // A failed or cancelled command's change isn't the app's: whatever the board did shows as an
                    // external one.
                    _expected.Forget(all.On, on: true);
                    if (ex is BoardNotConnectedException && !all.Off.IsEmpty)
                    {
                        await SaveAsync(db, events, now, CancellationToken.None);
                        RememberShutOffs(shutOff);
                    }

                    throw;
                }
            }

            // Pages also follow a sequence's own progress, e.g. "Starting" turning into "On since".
            var changed = events.Count > 0 || !all.Off.IsEmpty || !all.On.IsEmpty || db.ChangeTracker.HasChanges();
            await SaveAsync(db, events, now, cancellationToken);
            RememberShutOffs(shutOff);
            if (changed)
            {
                _changes.Notify();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes a group that is on outside the app (<see cref="EventActors.External"/>) the actor's, e.g. a schedule's
    /// during its window: no auto-off of its own then. Returns whether it did; a run that is someone else's by now is
    /// left alone.
    /// </summary>
    public async Task<bool> TakeOverOwnerlessAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        return await ExclusiveAsync(
            async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
                var runtime = await db.GroupRuntimes.SingleOrDefaultAsync(r => r.GroupId == groupId, cancellationToken);
                if (runtime?.TurnedOnBy != EventActors.External)
                {
                    return false;
                }

                runtime.TurnedOnBy = actor;
                runtime.AutoOffAtUtc = null;
                await db.SaveChangesAsync(cancellationToken);
                _changes.Notify();
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs a change to the groups themselves (create, save, delete) under the same lock as the sequence, so a check
    /// like "the group is off" still holds when the change is saved.
    /// </summary>
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await change();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync(ContactorDbContext db, List<(int GroupId, GroupEvent Event)> events, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var (groupId, e) in events)
        {
            db.EventLog.Add(new EventLogEntry
            {
                TimestampUtc = now,
                BoardId = BoardId,
                GroupId = groupId,
                RelayId = e.RelayId,
                Actor = e.Actor,
                Action = e.Action,
                Details = e.Details,
            });
            if (e.Action == EventActions.InterlockTrip)
            {
                LogInterlockTrip(groupId, BoardId);
            }
        }

        events.Clear();
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // From the board's read loop: each status is a reason to look again.
    private void OnStateChanged(object? sender, BoardState state)
    {
        if (state.IsConnected && !_wasConnected)
        {
            Interlocked.Exchange(ref _reconnected, 1);
        }

        _wasConnected = state.IsConnected;
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already awake.
            }
        }
    }

    private delegate string? GroupRequest(
        RelayGroup group,
        IReadOnlyList<GroupMemberInfo> members,
        BoardState state,
        GroupRuntime runtime,
        DateTime now,
        GroupCommands commands);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Interlock tripped in relay group {GroupId} on relay board {BoardId}")]
    private partial void LogInterlockTrip(int groupId, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay group step on relay board {BoardId} failed")]
    private partial void LogStepFailed(Exception exception, int boardId);
}
