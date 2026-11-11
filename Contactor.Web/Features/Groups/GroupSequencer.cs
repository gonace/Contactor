using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contactor.Web.Features.Groups;

/// <summary>
/// Runs the relay groups (<see cref="GroupPlanner"/> has the rules): on every status from the board and every second
/// it plans each group's next step, sends the commands, and saves the groups' <see cref="GroupRuntime"/> and the
/// events. Requests from people (start, stop, one relay on or off) go through the same lock, so one thing happens at a
/// time. While the board is disconnected nothing happens; run-on times keep counting, and on reconnect the interlock
/// is checked first, then whatever fell due meanwhile.
/// </summary>
public sealed partial class GroupSequencer : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IRelayBoard _board;
    private readonly IDbContextFactory<ContactorDbContext> _dbFactory;
    private readonly LocalClock _clock;
    private readonly ExpectedChanges _expected;
    private readonly RelayChanges _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GroupSequencer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private bool _wasConnected;
    private int _reconnected;

    public GroupSequencer(
        IRelayBoard board,
        IDbContextFactory<ContactorDbContext> dbFactory,
        LocalClock clock,
        ExpectedChanges expected,
        RelayChanges changes,
        TimeProvider timeProvider,
        ILogger<GroupSequencer> logger)
    {
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _expected = expected;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;

        // From the start, not from ExecuteAsync (a background thread): no status may go unseen.
        _board.StateChanged += OnStateChanged;
    }

    public override void Dispose()
    {
        _board.StateChanged -= OnStateChanged;
        _gate.Dispose();
        _wake.Dispose();
        base.Dispose();
    }

    /// <summary>The relays that are in a group.</summary>
    public async Task<ChannelMask> MemberMaskAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return ChannelMask.Of(await db.RelayGroupMembers.Select(m => m.RelayId).ToListAsync(cancellationToken));
    }

    /// <summary>Plans and runs the next step of every group. Does nothing while the board is disconnected.</summary>
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

    /// <summary>Stops every group, with run-on times ("Alla av").</summary>
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
    /// Turns one relay of a group on or off, within the interlock. Returns why it can't, in Swedish, or null when
    /// done. Throws <see cref="BoardNotConnectedException"/> without the board.
    /// </summary>
    public async Task<string?> SetMemberAsync(int groupId, int relayId, bool on, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        string? refusal = "Reläet ingår inte i gruppen.";
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                LogStepFailed(ex);
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
        var delay = Task.Delay(Interval, _timeProvider, cts.Token);
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
            if (!state.IsConnected)
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
            var groups = await db.RelayGroups.Include(g => g.Members).OrderBy(g => g.Id).ToListAsync(cancellationToken);
            if (groups.Count == 0)
            {
                return;
            }

            var runtimes = await db.GroupRuntimes.ToDictionaryAsync(r => r.GroupId, cancellationToken);
            var all = new GroupCommands();
            List<(int GroupId, GroupEvent Event)> events = [];
            foreach (var group in groups)
            {
                if (!runtimes.TryGetValue(group.Id, out var runtime))
                {
                    runtime = new GroupRuntime { GroupId = group.Id };
                    db.GroupRuntimes.Add(runtime);
                }

                var members = group.Members
                    .OrderBy(m => m.Position)
                    .Select(m => new GroupMemberInfo(m.Position, m.RelayId, m.RoleName, m.StartDelaySec, m.RunOnSec))
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
                        all.On |= ChannelMask.Of(lost.RelayId);
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
                await _board.TurnOffAsync(all.Off, cancellationToken);
            }

            if (!all.On.IsEmpty)
            {
                _expected.Expect(all.On, on: true);
                try
                {
                    await _board.TurnOnAsync(all.On, cancellationToken);
                }
                catch (BoardNotConnectedException) when (!all.Off.IsEmpty)
                {
                    await SaveAsync(db, events, now, CancellationToken.None);
                    throw;
                }
            }

            // Pages also follow a sequence's own progress, e.g. "Startar" turning into "På sedan".
            var changed = events.Count > 0 || !all.Off.IsEmpty || !all.On.IsEmpty || db.ChangeTracker.HasChanges();
            await SaveAsync(db, events, now, cancellationToken);
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
                GroupId = groupId,
                RelayId = e.RelayId,
                Actor = e.Actor,
                Action = e.Action,
                Details = e.Details,
            });
            if (e.Action == EventActions.InterlockTrip)
            {
                LogInterlockTrip(groupId);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Interlock tripped in relay group {GroupId}")]
    private partial void LogInterlockTrip(int groupId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay group step failed")]
    private partial void LogStepFailed(Exception exception);
}
