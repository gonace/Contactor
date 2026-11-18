using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Features.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Localization;

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
    private readonly IStringLocalizer<Texts> _texts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _ended = new();
    private bool _wasConnected;

    // The relays an OFF was sent for that the board's status doesn't show yet, as of the pass under way. Under _gate.
    private ChannelMask _offOnItsWay;

    // The relays an ON was sent for that the board's status doesn't show yet, as of the pass under way. Under _gate.
    private ChannelMask _onOnItsWay;
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
        ILogger<BoardSequencer> logger,
        IStringLocalizer<Texts> texts)
    {
        _texts = texts;
        _board = board;
        _dbFactory = dbFactory;
        _clock = clock;
        _expected = board.Expected;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;

        // From its making, before the board's connection starts: no status may go unseen. A change of the board's own
        // rules alone too: a relay reset from the board controlling it by itself gets the interlock's commands at once.
        _board.StateChanged += OnStateChanged;
        _board.OwnRulesChanged += OnOwnRulesChanged;
    }

    /// <inheritdoc/>
    public int BoardId => _board.BoardId;

    /// <summary>The board it runs the groups of.</summary>
    public LiveBoard Board => _board;

    /// <summary>
    /// Stops listening and ends the loop. A step or request under way finishes first, so nothing is left half sent and
    /// unsaved; a request that comes after throws <see cref="BoardNotConnectedException"/>, and a step does nothing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _board.StateChanged -= OnStateChanged;
        _board.OwnRulesChanged -= OnOwnRulesChanged;

        // The step or request under way first, while the board's connection still runs; then the loop ends.
        await _gate.WaitAsync(CancellationToken.None);
        _gate.Release();
        await _ended.CancelAsync();

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

    // A relay's name in the events: its step's when it is the step's only relay, otherwise its own.
    private static string NameOf(RelayGroupMember member)
    {
        return member.Step!.Members.Count == 1 ? member.Step.Name : member.Relay!.Name;
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

    /// <summary>
    /// Starts a group, or carries on from where it is. Throws <see cref="BoardNotConnectedException"/> without the board,
    /// and <see cref="GroupUnavailableException"/>, sending nothing for it, while the board controls one of its relays by
    /// itself (<see cref="BoardState.Unavailable"/>): it would ignore that relay's ON, and the group's order with it.
    /// </summary>
    public async Task StartGroupAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var unavailable = false;
        await RunAsync(
            (group, steps, state, runtime, now, commands) =>
            {
                if (group.Id == groupId && IsUnavailable(steps))
                {
                    unavailable = true;
                }
                else if (group.Id == groupId)
                {
                    // A schedule's OffTime is the end of the run it starts; it gets no auto-off.
                    var maxOnMinutes = actor == EventActors.Schedule ? null : group.MaxOnMinutes;
                    GroupPlanner.Start(steps, state, runtime, actor, maxOnMinutes, now, commands, _offOnItsWay);
                }

                return null;
            },
            cancellationToken);
        if (unavailable)
        {
            throw new GroupUnavailableException();
        }
    }

    // A stop whose next OFF is for a relay the board controls by itself and shows on can't happen: the board ignores it.
    // The stop ends there, as given up, without sending it again: what is still on stays on (above it, e.g. the fan
    // keeps running), and the interlock still acts on every status.
    private void GiveUpStopTheBoardIgnores(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime)
    {
        if (runtime.Phase != GroupPhases.Stopping || runtime.PendingOn || runtime.PendingStep is not { } step || step < 1 || step > steps.Count)
        {
            return;
        }

        var unavailable = state.Unavailable;
        if (!steps[step - 1].Relays.Any(r => (runtime.PendingRelayId is not { } id || r.RelayId == id) && unavailable.Contains(r.Channel) && state.IsOn(r.Channel)))
        {
            return;
        }

        runtime.Phase = GroupPhases.Idle;
        runtime.NextStepAtUtc = null;
        runtime.StopRelayIds = [];
        runtime.PendingStep = null;
        runtime.PendingRelayId = null;
        runtime.PendingOn = false;
        runtime.PendingSinceUtc = null;
    }

    // Whether the board controls one of the group's relays by itself now.
    private bool IsUnavailable(IReadOnlyList<GroupStepInfo> steps)
    {
        var unavailable = _board.State.Unavailable;
        return GroupPlanner.Relays(steps).Any(r => unavailable.Contains(r.Relay.Channel));
    }

    /// <summary>Stops a group, with its run-on times. Throws <see cref="BoardNotConnectedException"/> without the board.</summary>
    public async Task StopGroupAsync(int groupId, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        await RunAsync(
            (group, steps, state, runtime, now, commands) =>
            {
                if (group.Id == groupId)
                {
                    GroupPlanner.Stop(steps, state, runtime, actor, now, commands, _onOnItsWay);
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
            (group, steps, state, runtime, now, commands) =>
            {
                GroupPlanner.Stop(steps, state, runtime, actor, now, commands, _onOnItsWay);
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
        // In words for the person who asked: the request runs in their own flow, so in the app's language.
        string? refusal = _texts["Groups.Refusal.NotInGroup"];
        await RunAsync(
            (group, steps, state, runtime, now, commands) =>
            {
                if (group.Id != groupId)
                {
                    return null;
                }

                // Its position in the group's start order, from 1.
                var position = GroupPlanner.Relays(steps).Select(r => r.Relay.RelayId).ToList().IndexOf(relayId) + 1;
                if (position == 0)
                {
                    return null;
                }

                refusal = null;
                if (on && GroupPlanner.Relays(steps).ElementAt(position - 1) is var (step, relay) && _board.State.Unavailable.Contains(relay.Channel))
                {
                    // The board ignores its ON: nothing is sent. Named as the group's warning names it: by its step when
                    // it is the step's only relay, otherwise by its own name.
                    refusal = _texts["Groups.Refusal.Unavailable", step.Relays.Count == 1 ? step.Name : relay.Name].Value;
                }
                else if (on)
                {
                    refusal = GroupPlanner.MemberOn(steps, state, runtime, position, actor, group.MaxOnMinutes, now, commands, _offOnItsWay) is { } why
                        ? _texts["Groups.Refusal.NeedsOn", why.NeedsOn].Value
                        : null;
                }
                else
                {
                    GroupPlanner.MemberOff(steps, state, runtime, position, actor, now, commands, _onOnItsWay);
                }

                return refusal;
            },
            cancellationToken,
            on ? (groupId, relayId) : null);
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
    // means nothing is sent or saved for it. A request to turn one relay of a group on names it (member): when the board
    // doesn't take that ON, nothing of the request is kept and BoardNotConnectedException is thrown after the save.
    private async Task RunAsync(GroupRequest? request, CancellationToken cancellationToken, (int GroupId, int RelayId)? member = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = _board.State;

            // A relay the app sent an OFF for still shows on until the board's status catches up: it counts as off for
            // what may come on, so a status from before the OFF never lets a relay below it on.
            _offOnItsWay = ChannelMask.Of(state.Relays.Channels.Where(c => _expected.IsPending(c, on: false)));

            // A relay turned on again while the others of its step stop: that stop finishes only once the board shows it.
            // "Turn off" turns it off too, after that ON.
            _onOnItsWay = ChannelMask.Of(_board.Capabilities.AllRelays.Except(state.Relays).Channels.Where(c => _expected.IsPending(c, on: true)));
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
            var groups = await db.RelayGroups.Include(g => g.Steps).ThenInclude(s => s.Members).ThenInclude(m => m.Relay)
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

            // The ONs of groups that are stopping: a run-on's relay (after a trip, the one above the gap). They go even
            // while the board is closed for its removal.
            var runOn = ChannelMask.None;

            // The member's ON the request planned: its group's runtime as before the request, so a refused ON can be
            // taken back.
            MemberOnRequest? memberOn = null;
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
                    var inOrder = group.Members.OrderBy(m => m.Step!.Position).ThenBy(m => m.Order).ToList();
                    var on = inOrder.Where(m => m.Relay!.BoardId == BoardId && state.IsOn(Channel(m))).ToList();
                    all.Off |= ChannelMask.Of(on.Select(Channel));
                    runtime.Phase = GroupPhases.Idle;
                    runtime.TargetLevel = 0;
                    runtime.NextStepAtUtc = null;
                    runtime.PendingStep = null;
                    runtime.PendingRelayId = null;
                    runtime.PendingOn = false;
                    runtime.PendingSinceUtc = null;
                    runtime.StopRelayIds = [];
                    runtime.OnSinceUtc = null;
                    runtime.AutoOffAtUtc = null;
                    runtime.TurnedOnBy = null;
                    if (on.Count > 0 && shutOff.Add(group.Id) && !_shutOffLogged.Contains(group.Id))
                    {
                        // Sent again on every step until the board confirms it, but logged once.
                        // "Fan turned off. Heat must be on first.": the relay the board doesn't have can't be on.
                        var missing = inOrder.First(m => !IsOnBoard(m));
                        var details = JsonSerializer.Serialize(new { missing = NameOf(missing), off = on.Select(NameOf) });
                        events.Add((group.Id, new GroupEvent(EventActions.InterlockTrip, missing.RelayId, EventActors.System, details)));
                    }

                    continue;
                }

                var steps = GroupSteps.Of(group);
                var lostStep = (int?)null;
                if (reconnected && runtime.PendingStep is { } pending)
                {
                    // A command from before the connection dropped (or the app restarted) was done or lost. The board
                    // gets a fresh timeout to show it; a confirmation then times the next step as usual, so no
                    // start delay or run-on is skipped.
                    runtime.PendingSinceUtc = now;
                    if (runtime.PendingOn && runtime.Phase == GroupPhases.Stopping)
                    {
                        lostStep = pending;
                    }
                }

                var commands = new GroupCommands();
                var before = member?.GroupId == group.Id ? db.Entry(runtime).CurrentValues.Clone() : null;
                if (request is not null && request(group, steps, state, runtime, now, commands) is not null && commands.IsEmpty)
                {
                    // Refused: nothing for this group this time.
                    continue;
                }

                // Only the request's own ON, the one it logs: an ON a trip sends for its run-on is the trip's, and
                // stays the trip's whatever the board does with it.
                if (before is not null && member is { } asked
                    && commands.Events.Any(e => e.Action == EventActions.On && e.RelayId == asked.RelayId)
                    && group.Members.FirstOrDefault(m => m.RelayId == asked.RelayId) is { } asking)
                {
                    memberOn = new MemberOnRequest(group, runtime, before, Channel(asking));
                }

                if (commands.IsEmpty)
                {
                    GroupPlanner.Plan(steps, state, runtime, now, commands, _offOnItsWay, _onOnItsWay);
                }

                GroupPlanner.GiveOwnerlessAutoOff(steps, state, runtime, group.MaxOnMinutes, now);

                // The relays a stop (a trip) needs for its run-on, e.g. the fans after the heaters, are sent once more
                // in case their ON was lost; after the interlock has had its say, only while every step above them is
                // still all on (a relay above may have gone off during the outage), and never a relay this pass turns
                // off.
                if (lostStep is { } step && step >= 1 && step <= steps.Count
                    && runtime.Phase == GroupPhases.Stopping && runtime.PendingOn && runtime.PendingStep == step
                    && GroupPlanner.Level(steps, state) >= step - 1)
                {
                    var lost = ChannelMask.Of(steps[step - 1].Relays
                        .Where(r => runtime.PendingRelayId is not { } id || r.RelayId == id)
                        .Select(r => r.Channel))
                        .Except(commands.Off)
                        .Except(all.Off);
                    commands.On |= lost;
                }

                GiveUpStopTheBoardIgnores(steps, state, runtime);
                all.Off |= commands.Off;
                all.On |= commands.On;
                if (runtime.Phase == GroupPhases.Stopping)
                {
                    runOn |= commands.On;
                }
                events.AddRange(commands.Events.Select(e => (group.Id, e)));
            }

            // A relay the board controls by itself (BoardState.Unavailable) ignores the app's commands: none is sent to it,
            // so nothing goes out again and again while the planning, the interlock's included, stays as it is. Only a
            // stop's run-on ON goes, which may be all that keeps a relay below running safely. Once the relay is reset,
            // the next status plans and sends as ever.
            var unavailable = state.Unavailable;
            all.Off = all.Off.Except(unavailable);
            all.On = all.On.Except(unavailable.Except(runOn));

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

            var refusedMember = false;
            if (!all.On.IsEmpty)
            {
                _expected.Expect(all.On, on: true);
                try
                {
                    // While the board is closed, only a stop's run-on goes; anything else is refused as a whole, as
                    // before: nothing of this step's planning is saved, and the next step plans again.
                    var taken = runOn.ContainsAll(all.On)
                        ? await _board.TurnOnForRunOnAsync(all.On, cancellationToken)
                        : await _board.TurnOnAsync(all.On, cancellationToken);
                    var refused = all.On.Except(taken).Except(_board.State.Relays);
                    _expected.Forget(refused, on: true);
                    refusedMember = TakeBackRefused(db, memberOn, refused, events);
                }
                catch (Exception ex)
                {
                    // A failed or cancelled command's change isn't the app's: whatever the board did shows as an
                    // external one.
                    _expected.Forget(all.On, on: true);

                    // The OFF went out: it is recorded, also when the caller cancelled before the ON.
                    if ((ex is BoardNotConnectedException || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)) && !all.Off.IsEmpty)
                    {
                        TakeBackRefused(db, memberOn, all.On.Except(_board.State.Relays), events);
                        await SaveAsync(db, events, now, CancellationToken.None);
                        RememberShutOffs(shutOff);
                    }

                    throw;
                }
            }

            // Pages also follow a sequence's own progress, e.g. "Starting" turning into "On since".
            var changed = events.Count > 0 || !all.Off.IsEmpty || !all.On.IsEmpty || db.ChangeTracker.HasChanges();

            // What went out is recorded whatever happens to the caller now: a cancel (mostly at shutdown) would leave
            // the board's change without its run and events.
            var sent = !all.Off.IsEmpty || !all.On.IsEmpty;
            await SaveAsync(db, events, now, sent ? CancellationToken.None : cancellationToken);
            RememberShutOffs(shutOff);
            if (changed)
            {
                _changes.Notify();
            }

            if (refusedMember)
            {
                // As for a relay of its own: the person is told the board didn't do it.
                throw new BoardNotConnectedException("The board didn't take the command.");
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
        Wake();
    }

    // Only the board's own rules changed: a relay it controlled by itself may take commands again.
    private void OnOwnRulesChanged(object? sender, BoardState state)
    {
        Wake();
    }

    private void Wake()
    {
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

    // A member's ON the board didn't take is no change of the app's: its On event goes, and its group's runtime is as
    // before the request, so a run doesn't become the person's and a stop it called off still goes. Only that group's:
    // the others' events and runtimes stay as planned. Returns whether it took one back.
    private static bool TakeBackRefused(ContactorDbContext db, MemberOnRequest? memberOn, ChannelMask refused, List<(int GroupId, GroupEvent Event)> events)
    {
        if (memberOn is not { } request || !refused.Contains(request.Channel))
        {
            return false;
        }

        db.Entry(request.Runtime).CurrentValues.SetValues(request.Before);
        var refusedRelays = request.Group.Members.Where(m => refused.Contains(Channel(m))).Select(m => m.RelayId).ToHashSet();
        events.RemoveAll(e => e.GroupId == request.Group.Id && e.Event.Action == EventActions.On
            && e.Event.RelayId is { } relayId && refusedRelays.Contains(relayId));
        return true;
    }

    private sealed record MemberOnRequest(RelayGroup Group, GroupRuntime Runtime, PropertyValues Before, int Channel);

    private delegate string? GroupRequest(
        RelayGroup group,
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        DateTime now,
        GroupCommands commands);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Interlock tripped in relay group {GroupId} on relay board {BoardId}")]
    private partial void LogInterlockTrip(int groupId, int boardId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay group step on relay board {BoardId} failed")]
    private partial void LogStepFailed(Exception exception, int boardId);
}

/// <summary>
/// A group was to start while its board controls one of its relays by itself and ignores commands for it (e.g. a
/// Waveshare relay in linkage mode): nothing was sent. A person resets that relay to start the group.
/// </summary>
public sealed class GroupUnavailableException : InvalidOperationException
{
    public GroupUnavailableException()
        : base("The board controls a relay of the group itself; reset it to start the group.")
    {
    }

    public GroupUnavailableException(string message)
        : base(message)
    {
    }

    public GroupUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
