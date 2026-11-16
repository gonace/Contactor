using System.Text.Json;
using Contactor.Boards;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Groups;

/// <summary>Why a relay of a group can't be turned on: the step above it, by its name in the group, isn't all on.</summary>
public sealed record MemberRefusal(string NeedsOn);

/// <summary>A relay of a group's step, as the planner sees it.</summary>
/// <param name="RelayId">The relay, for the events.</param>
/// <param name="Channel">Its channel on the board: what the board's state and the commands go by.</param>
/// <param name="Name">The relay's own name.</param>
public sealed record GroupRelayInfo(int RelayId, int Channel, string Name);

/// <summary>
/// A step of a group, as the planner sees it: one or more relays that switch together. Its place in the list is its
/// place in the start order.
/// </summary>
/// <param name="Name">Its name in the group, e.g. "Fans".</param>
/// <param name="Relays">Its relays, in their order in the step; at least one.</param>
public sealed record GroupStepInfo(string Name, int StartDelaySec, int RunOnSec, IReadOnlyList<GroupRelayInfo> Relays);

/// <summary>An event the planner wants logged, with the group's id added by the caller.</summary>
public sealed record GroupEvent(string Action, int? RelayId, string Actor, string? Details);

/// <summary>What to send to the board and log after a planning step. OFF goes first, so a relay never runs alone below a gap.</summary>
public sealed class GroupCommands
{
    public ChannelMask Off { get; set; }

    public ChannelMask On { get; set; }

    public List<GroupEvent> Events { get; } = [];

    public bool IsEmpty => Off.IsEmpty && On.IsEmpty && Events.Count == 0;
}


/// <summary>
/// The rules of a relay group, apart from the board and the database: given the steps in start order, which of their
/// relays the board says are on, the group's <see cref="GroupRuntime"/> and the time, it decides the next commands and
/// updates the runtime. <see cref="GroupSequencer"/> runs it on every status from the board and every second.
/// <list type="bullet">
/// <item>Start: from the top, the relays of a step together; the next step only once the board confirms every relay of
/// the one before is on and its start delay has passed. No confirmation within <see cref="ConfirmTimeout"/>: the start
/// stops, what runs keeps running.</item>
/// <item>Stop: from the bottom, the relays of a step together; the lowest step goes off at once, each one above it
/// after its run-on time.</item>
/// <item>Interlock: a relay may only be on while every relay of every step above it is. Checked first, every time,
/// whatever turned the relays on: a broken interlock turns off everything below the gap at once and starts the
/// missing relays, so their step can run on and then stop as in a normal stop.</item>
/// </list>
/// One relay is named by its position in the group's start order, from 1: the steps' relays one after another.
/// </summary>
public static class GroupPlanner
{
    /// <summary>How long the board has to confirm a command before the step counts as failed.</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many steps from the top are all on, without a gap.</summary>
    public static int Level(IReadOnlyList<GroupStepInfo> steps, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        var level = 0;
        while (level < steps.Count && IsFull(steps[level], state))
        {
            level++;
        }

        return level;
    }

    /// <summary>The position of the lowest step with a relay on, or 0 when none is.</summary>
    public static int Reach(IReadOnlyList<GroupStepInfo> steps, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        for (var p = steps.Count; p >= 1; p--)
        {
            if (steps[p - 1].Relays.Any(r => state.IsOn(r.Channel)))
            {
                return p;
            }
        }

        return 0;
    }

    /// <summary>Whether every relay of the step is on.</summary>
    public static bool IsFull(GroupStepInfo step, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(state);
        return step.Relays.All(r => state.IsOn(r.Channel));
    }

    /// <summary>Whether any relay of the group is on.</summary>
    public static bool AnyOn(IReadOnlyList<GroupStepInfo> steps, BoardState state)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        return steps.Any(s => s.Relays.Any(r => state.IsOn(r.Channel)));
    }

    /// <summary>Every relay of the group, in start order: the steps' relays one after another.</summary>
    public static IEnumerable<(GroupStepInfo Step, GroupRelayInfo Relay)> Relays(IReadOnlyList<GroupStepInfo> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return steps.SelectMany(s => s.Relays.Select(r => (s, r)));
    }

    /// <summary>The relay at a position in the group's start order, from 1, with its step.</summary>
    public static (GroupStepInfo Step, GroupRelayInfo Relay) RelayAt(IReadOnlyList<GroupStepInfo> steps, int position)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        foreach (var step in steps)
        {
            if (position <= step.Relays.Count)
            {
                return (step, step.Relays[position - 1]);
            }

            position -= step.Relays.Count;
        }

        throw new ArgumentOutOfRangeException(nameof(position), "The group has fewer relays.");
    }

    /// <summary>
    /// A relay's name in the events and the status: its step's name when it is the step's only relay ("Fan"), otherwise
    /// its own ("Fan 1").
    /// </summary>
    public static string NameOf(GroupStepInfo step, GroupRelayInfo relay)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(relay);
        return step.Relays.Count == 1 ? step.Name : relay.Name;
    }

    /// <summary>
    /// The name of the step whose relays must all be on before the relay at <paramref name="position"/> may come on
    /// ("Needs Fans to be on"), or null when nothing holds it back.
    /// </summary>
    public static string? NeedsOn(IReadOnlyList<GroupStepInfo> steps, BoardState state, int position)
    {
        ArgumentNullException.ThrowIfNull(state);
        var (step, _) = RelayAt(steps, position);
        var above = IndexOf(steps, step);
        return above > 0 && !IsFull(steps[above - 1], state) ? steps[above - 1].Name : null;
    }

    /// <summary>The relays that keep running during a stop's run-on: the lowest step's that are on, or the one relay a person turned off.</summary>
    public static IReadOnlyList<GroupRelayInfo> RunningOn(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        var reach = Reach(steps, state);
        return runtime.Phase != GroupPhases.Stopping || reach < 1 ? [] : GoingOff(steps[reach - 1], state, runtime);
    }

    /// <summary>
    /// A group that is on with nobody's run (turned on at the board, during an outage, or by a command the app was told
    /// failed) gets "outside the app" from now, with the group's own auto-off, so nothing runs for ever by accident.
    /// A group under way (starting, stopping or a command not yet confirmed) is left to its sequence.
    /// </summary>
    public static void GiveOwnerlessAutoOff(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime, int? maxOnMinutes, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.OnSinceUtc is not null || runtime.Phase != GroupPhases.Idle || runtime.PendingStep is not null || !AnyOn(steps, state))
        {
            return;
        }

        runtime.OnSinceUtc = now;
        runtime.TurnedOnBy = EventActors.External;
        runtime.AutoOffAtUtc = maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
    }

    /// <summary>The next step for one group. Updates <paramref name="runtime"/> and adds to <paramref name="commands"/>.</summary>
    /// <param name="offOnItsWay">
    /// Relays an OFF was sent for that the board hasn't shown yet: they count as off for what may come on (a start's
    /// next step, a confirmed ON), so a status from before that OFF never confirms them on.
    /// </param>
    /// <param name="onOnItsWay">
    /// Relays an ON was sent for that the board hasn't shown yet. A stop of some relays of a step doesn't finish while a
    /// relay turned on again during it still waits for its ON, so its run isn't taken for one outside the app. A stop of
    /// the whole group counts them as on: it turns them off too, from the bottom, and their OFF counts as done only once
    /// that ON is no longer on its way. A stop of some relays counts the relays it stops, and those of the step just
    /// below whose OFF it waits for, as on in the same way.
    /// </param>
    public static void Plan(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        DateTime now,
        GroupCommands commands,
        ChannelMask offOnItsWay = default,
        ChannelMask onOnItsWay = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        var n = steps.Count;
        if (n == 0)
        {
            return;
        }

        // What may count as on for turning on more: not a relay with an OFF on its way. The interlock and the stop go
        // by what the board shows.
        var onState = Without(state, offOnItsWay);
        bool On(GroupRelayInfo relay) => onState.IsOn(relay.Channel);
        var level = Level(steps, onState);
        var stopState = StopState(steps, state, runtime, onOnItsWay);
        var reach = Reach(steps, stopState);

        // 1. The interlock, before anything else.
        if (Interlock(steps, state, runtime, now, commands))
        {
            return;
        }

        // 2. A command the board hasn't confirmed yet.
        if (runtime.PendingStep is not null)
        {
            if (Pending(steps, runtime) is not { } pending)
            {
                // A step or relay the group no longer has.
                ClearPending(runtime);
            }
            else if (pending.Relays.All(r => (runtime.PendingOn ? On(r) : stopState.IsOn(r.Channel)) == runtime.PendingOn))
            {
                ClearPending(runtime);
                Confirmed(steps, stopState, runtime, level, reach, now, commands);
            }
            else if (now - (runtime.PendingSinceUtc ?? now) >= ConfirmTimeout)
            {
                if (runtime.PendingOn && runtime.Phase != GroupPhases.Idle)
                {
                    // Relays that didn't come on: a step of a start, or the relays a trip started to run on. Never
                    // sent again: what runs keeps running, and a stop carries on without them.
                    var failed = pending.Relays.Where(r => !On(r)).ToList();
                    commands.Events.Add(new GroupEvent(
                        EventActions.GroupStartFailed,
                        RelayIdOf(failed),
                        EventActors.System,
                        JsonSerializer.Serialize(new { step = pending.Position, role = NameOf(pending.Step, failed) })));
                    ClearPending(runtime);
                    runtime.NextStepAtUtc = null;
                    if (runtime.Phase == GroupPhases.Starting)
                    {
                        runtime.Phase = GroupPhases.Idle;
                        runtime.TargetLevel = level;
                        return;
                    }

                    // The stop carries on: the lowest step still on runs on first, as after any confirmed step.
                    Confirmed(steps, stopState, runtime, level, reach, now, commands);
                }
                else if (runtime.Phase == GroupPhases.Idle)
                {
                    ClearPending(runtime);
                }
                else
                {
                    // A stop must happen: send it again.
                    commands.Off |= Mask(pending.Relays);
                    runtime.PendingSinceUtc = now;
                    return;
                }
            }
            else
            {
                return;
            }
        }

        // 3. The sequence.
        if (runtime.Phase == GroupPhases.Starting)
        {
            if (level >= Math.Min(runtime.TargetLevel, n))
            {
                runtime.Phase = GroupPhases.Idle;
                runtime.NextStepAtUtc = null;
                return;
            }

            if (runtime.NextStepAtUtc is null || now >= runtime.NextStepAtUtc)
            {
                // Every relay of the next step together; one already on (e.g. turned on by hand) is left as it is.
                commands.On |= Mask(steps[level].Relays.Where(r => !On(r)));
                SetPending(runtime, level + 1, relayId: null, on: true, now);
            }

            return;
        }

        if (runtime.Phase == GroupPhases.Stopping)
        {
            if (StopDone(steps, stopState, runtime, reach))
            {
                if (BroughtBackOnItsWay(steps, state, runtime, onOnItsWay))
                {
                    // A relay turned on again while the others of its step stopped: its ON took its own OFF over, and
                    // the stop finishes once the board shows it on, so the run stays the person's.
                    return;
                }

                runtime.Phase = GroupPhases.Idle;
                runtime.NextStepAtUtc = null;
                runtime.StopRelayIds = [];
                if (reach == 0)
                {
                    ClearRun(runtime);
                }

                return;
            }

            if (runtime.NextStepAtUtc is null || now >= runtime.NextStepAtUtc)
            {
                var step = steps[reach - 1];
                var going = GoingOff(step, stopState, runtime);
                var ranOn = runtime.NextStepAtUtc is not null && reach < n && step.RunOnSec > 0;
                commands.Off |= Mask(going);
                SetPending(runtime, reach, relayId: null, on: false, now);
                runtime.NextStepAtUtc = null;
                if (ranOn)
                {
                    commands.Events.Add(new GroupEvent(
                        EventActions.RunOnEnd,
                        RelayIdOf(going),
                        EventActors.System,
                        JsonSerializer.Serialize(new { role = NameOf(step, going) })));
                }
            }

            return;
        }

        // Idle.
        if (reach == 0)
        {
            ClearRun(runtime);
        }
        else if (runtime.AutoOffAtUtc is { } autoOff && now >= autoOff)
        {
            var minutes = runtime.OnSinceUtc is { } since ? (int)Math.Round((autoOff - since).TotalMinutes) : (int?)null;
            BeginStop(runtime);
            commands.Events.Add(new GroupEvent(
                EventActions.GroupOff,
                null,
                EventActors.AutoOff,
                minutes is null ? null : JsonSerializer.Serialize(new { afterMinutes = minutes })));
            Plan(steps, state, runtime, now, commands, offOnItsWay, onOnItsWay);
        }
    }

    /// <summary>
    /// Starts the whole group, or carries on from where it is (e.g. during a run-on: nothing that runs is restarted).
    /// Returns false when it is already starting.
    /// </summary>
    public static bool Start(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        string actor,
        int? maxOnMinutes,
        DateTime now,
        GroupCommands commands,
        ChannelMask offOnItsWay = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        if (runtime.Phase == GroupPhases.Starting)
        {
            return false;
        }

        // An OFF on its way (the group was stopping) is overtaken by an ON, which is then waited for.
        var overtaken = !runtime.PendingOn ? Pending(steps, runtime) : null;
        var overtakenRelayId = runtime.PendingRelayId;
        runtime.Phase = GroupPhases.Starting;
        runtime.TargetLevel = steps.Count;
        runtime.NextStepAtUtc = null;
        runtime.StopRelayIds = [];
        ClearPending(runtime);
        if (overtaken is { } pending)
        {
            // Their OFF is on its way: the board's status may still show them on from before it, which doesn't
            // confirm this ON.
            commands.On |= Mask(pending.Relays);
            offOnItsWay |= Mask(pending.Relays);
            SetPending(runtime, pending.Position, overtakenRelayId, on: true, now);
        }

        if (runtime.OnSinceUtc is null || !AnyOn(steps, state))
        {
            runtime.OnSinceUtc = now;
        }

        runtime.TurnedOnBy = actor;
        runtime.AutoOffAtUtc = maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
        commands.Events.Add(new GroupEvent(EventActions.GroupOn, null, actor, Chain(steps)));
        Plan(steps, state, runtime, now, commands, offOnItsWay);
        return true;
    }

    /// <summary>Stops the whole group, from the bottom, with run-on times. Returns false when there is nothing to stop.</summary>
    /// <param name="onOnItsWay">
    /// Relays an ON was sent for that the board hasn't shown yet, e.g. one turned on again while the others of its step
    /// stopped: they count as on, so the stop turns them off too. Their OFF goes after that ON, and the board takes its
    /// commands in order.
    /// </param>
    public static bool Stop(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        string actor,
        DateTime now,
        GroupCommands commands,
        ChannelMask onOnItsWay = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        if (runtime.Phase == GroupPhases.Stopping)
        {
            // Already stopping the whole group (a trip's run-on relays on their way included): that stop carries on.
            if (runtime.TargetLevel == 0 && runtime.StopRelayIds.Count == 0)
            {
                return false;
            }

            // Stopping part of it (one relay turned off): that stop goes on to the whole group, and a run-on under way
            // keeps its time.
            var waiting = !runtime.PendingOn ? Pending(steps, runtime) : null;
            runtime.TargetLevel = 0;
            runtime.StopRelayIds = [];
            runtime.AutoOffAtUtc = null;
            commands.Events.Add(new GroupEvent(EventActions.GroupOff, null, actor, null));
            if (waiting is { } some && Pending(steps, runtime) is { } whole)
            {
                // Some relays of a step have their OFF on its way: the rest of that step goes with them now, also one
                // whose ON (turned on again) is still on its way, and the stop waits for all of them.
                var stopState = With(state, onOnItsWay);
                var more = whole.Relays.Where(r => !some.Relays.Contains(r) && stopState.IsOn(r.Channel)).ToList();
                if (more.Count > 0)
                {
                    commands.Off |= Mask(more);
                    runtime.PendingSinceUtc = now;
                }
            }

            Plan(steps, state, runtime, now, commands, onOnItsWay: onOnItsWay);
            return true;
        }

        // An ON on its way (a start, or one relay turned on) is overtaken: it gets an OFF, and the stop waits for the
        // board to confirm it before the step above runs on, as in any stop.
        var overtaken = runtime.PendingOn ? Pending(steps, runtime) : null;
        var overtakenRelayId = runtime.PendingRelayId;
        if (overtaken is null && !AnyOn(steps, With(state, onOnItsWay)) && runtime.Phase == GroupPhases.Idle)
        {
            return false;
        }

        BeginStop(runtime);
        commands.Events.Add(new GroupEvent(EventActions.GroupOff, null, actor, null));
        if (overtaken is { } pending)
        {
            commands.Off |= Mask(pending.Relays);
            SetPending(runtime, pending.Position, overtakenRelayId, on: false, now);
            return true;
        }

        Plan(steps, state, runtime, now, commands, onOnItsWay: onOnItsWay);
        return true;
    }

    /// <summary>
    /// Turns one relay on, within the interlock: only when every relay of the step above it is on. Returns why not, or
    /// null when done.
    /// </summary>
    /// <param name="position">The relay's position in the group's start order, from 1.</param>
    public static MemberRefusal? MemberOn(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        int position,
        string actor,
        int? maxOnMinutes,
        DateTime now,
        GroupCommands commands,
        ChannelMask offOnItsWay = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        var (step, relay) = RelayAt(steps, position);
        var place = IndexOf(steps, step) + 1;

        // The interlock first, as on every status: a relay below a gap is refused, and the trip goes on.
        var level = Level(steps, state);
        if (Interlock(steps, state, runtime, now, commands))
        {
            return place > level + 1 ? new MemberRefusal(steps[level].Name) : null;
        }

        // A relay with an OFF on its way counts as off, whatever the last status still shows: nothing may come on
        // below it, and turning it on again sends its ON.
        var pendingOff = !runtime.PendingOn && Pending(steps, runtime) is { } offs ? offs : ((int Position, GroupStepInfo Step, IReadOnlyList<GroupRelayInfo> Relays)?)null;
        var onState = Without(state, offOnItsWay | (pendingOff is { } p ? Mask(p.Relays) : ChannelMask.None));
        if (NeedsOn(steps, onState, position) is { } needs)
        {
            return new MemberRefusal(needs);
        }

        // A trip's run-on relays on their way: the trip's stop isn't called off, and nothing more is turned on.
        if (runtime.Phase == GroupPhases.Stopping && runtime.PendingOn)
        {
            return null;
        }

        // A step going off in a stop (its OFF on its way, or due now) counts as off already: a relay below it is refused,
        // and that OFF is never replaced or called off, so nothing runs below it even for a moment.
        if (GoingOffStep(steps, state, runtime, now) is { } goingOff && place > goingOff)
        {
            return new MemberRefusal(steps[goingOff - 1].Name);
        }

        // Relays turned off one at a time are stopping: turning one of them on again brings back that one only, and the
        // others still go off as asked. Below their step nothing may come on, as it would lose its step above.
        if (runtime.Phase == GroupPhases.Stopping && StoppingStep(steps, runtime) is { } stopping)
        {
            if (place > stopping)
            {
                return new MemberRefusal(steps[stopping - 1].Name);
            }

            if (place < stopping || (runtime.StopRelayIds.Count > 1 || !runtime.StopRelayIds.Contains(relay.RelayId)))
            {
                runtime.StopRelayIds = [.. runtime.StopRelayIds.Where(id => id != relay.RelayId)];
                BackOn(relay, onState, actor, maxOnMinutes, now, runtime, commands);
                return null;
            }

            // The only relay stopping: the stop is called off, as below.
        }

        // This relay's step is going off as a whole, its OFF on its way: this relay takes its OFF over with an ON, and
        // the others of its step still go off, as a stop of those relays.
        if (runtime.Phase == GroupPhases.Stopping && pendingOff is { } going && going.Position == place && going.Relays.Contains(relay))
        {
            var others = going.Relays.Where(r => r != relay && state.IsOn(r.Channel)).Select(r => r.RelayId).ToList();
            if (others.Count > 0)
            {
                runtime.TargetLevel = place - 1;
                runtime.StopRelayIds = others;
                runtime.NextStepAtUtc = null;
                BackOn(relay, onState, actor, maxOnMinutes, now, runtime, commands);
                return null;
            }
        }

        if (onState.IsOn(relay.Channel))
        {
            // E.g. the fan during its run-on: it stays on, and the stop is called off. A start going on is left alone.
            if (runtime.Phase != GroupPhases.Stopping)
            {
                return null;
            }

            runtime.Phase = GroupPhases.Idle;
            runtime.TargetLevel = Math.Max(Level(steps, onState), place);
            runtime.NextStepAtUtc = null;
            runtime.StopRelayIds = [];
            ClearPending(runtime);
            KeepsRunning(runtime, actor, maxOnMinutes, now);
            return null;
        }

        if (runtime.Phase == GroupPhases.Stopping)
        {
            KeepsRunning(runtime, actor, maxOnMinutes, now);
        }

        // Its ON, also when the last status still shows it on with its OFF on its way: the ON takes that OFF over, and
        // is waited for.
        commands.On |= ChannelMask.Of(relay.Channel);
        runtime.Phase = GroupPhases.Idle;
        runtime.TargetLevel = Math.Max(Level(steps, onState), place);
        runtime.NextStepAtUtc = null;
        runtime.StopRelayIds = [];
        SetPending(runtime, place, relay.RelayId, on: true, now);
        if (!AnyOn(steps, onState) || runtime.OnSinceUtc is null)
        {
            runtime.OnSinceUtc = now;
            runtime.AutoOffAtUtc = maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
        }

        runtime.TurnedOnBy = actor;
        commands.Events.Add(new GroupEvent(EventActions.On, relay.RelayId, actor, null));
        return null;
    }

    /// <summary>
    /// Turns one relay off: every relay of the steps below goes off at once, and this one keeps running for its step's
    /// run-on time first if any were on. The other relays of its step stay as they are. Nothing happens when it is off
    /// already and no ON for it is on its way.
    /// </summary>
    /// <param name="position">The relay's position in the group's start order, from 1.</param>
    /// <param name="onOnItsWay">
    /// Relays an ON was sent for that the board hasn't shown yet. One below this relay's step counts as on: it goes off
    /// with the others below, after its ON, so nothing below is left on without this relay. When it is in the step just
    /// below, this relay runs on only once that OFF is done. This relay itself counts as on too: its OFF goes after that
    /// ON, in its place, and counts as done only once that ON is no longer on its way and the board shows it off.
    /// </param>
    public static void MemberOff(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        int position,
        string actor,
        DateTime now,
        GroupCommands commands,
        ChannelMask onOnItsWay = default)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        var (step, relay) = RelayAt(steps, position);

        // The interlock first, as on every status; a trip stops the whole group, which this relay's OFF then keeps.
        Interlock(steps, state, runtime, now, commands);

        // A relay whose ON is on its way counts as on, or that ON would leave it on once it lands.
        if (!With(state, onOnItsWay).IsOn(relay.Channel))
        {
            return;
        }

        var place = IndexOf(steps, step) + 1;
        if (runtime.Phase == GroupPhases.Stopping && runtime.TargetLevel <= place - 1)
        {
            // A stop already under way goes at least as far: it is kept, never cut short to this relay, so nothing it
            // stops is left on (e.g. the group stopping and Fan 1 turned off during the fans' run-on: Fan 2 still stops).
            // One that stops only some relays of this relay's step takes this one too: it goes off with them, after
            // the relays below and its step's run-on.
            commands.Events.Add(new GroupEvent(EventActions.Off, relay.RelayId, actor, null));
            var stopsIt = runtime.TargetLevel < place - 1 || runtime.StopRelayIds.Count == 0 || runtime.StopRelayIds.Contains(relay.RelayId);
            if (!stopsIt)
            {
                runtime.StopRelayIds = [.. runtime.StopRelayIds, relay.RelayId];
                if (runtime.PendingStep == place && !runtime.PendingOn)
                {
                    // Its step's OFF is on its way already: this one goes with it now, and the stop waits for it too.
                    commands.Off |= ChannelMask.Of(relay.Channel);
                }
                else
                {
                    // Its step's OFF may be due now.
                    Plan(steps, state, runtime, now, commands, onOnItsWay: onOnItsWay);
                }
            }

            return;
        }

        runtime.Phase = GroupPhases.Stopping;
        runtime.TargetLevel = place - 1;
        runtime.StopRelayIds = [relay.RelayId];
        runtime.NextStepAtUtc = null;
        ClearPending(runtime);
        // A relay below with its ON on its way counts as on: its OFF goes after that ON, as the board takes its commands
        // in order.
        var belowState = With(state, onOnItsWay);
        var below = Mask(steps.Skip(place).SelectMany(s => s.Relays).Where(r => belowState.IsOn(r.Channel)));
        commands.Events.Add(new GroupEvent(EventActions.Off, relay.RelayId, actor, null));
        if (!below.IsEmpty)
        {
            // The ones below at once; this one runs on once they are confirmed off.
            commands.Off |= below;
            SetPending(runtime, place + 1, relayId: null, on: false, now);
            return;
        }

        Plan(steps, state, runtime, now, commands, onOnItsWay: onOnItsWay);
    }

    /// <summary>"Fans → 3 s → Heat", for the log.</summary>
    public static string Chain(IReadOnlyList<GroupStepInfo> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return JsonSerializer.Serialize(new
        {
            steps = steps.Select(s => s.Name).ToArray(),
            delays = steps.Skip(1).Select(s => s.StartDelaySec).ToArray(),
        });
    }

    // The interlock: only the lowest step on may be partly on; below a step that isn't all on, nothing may be. A broken
    // one turns off everything below the gap and starts the missing relays, so their step runs on, then stops. Returns
    // whether it was broken.
    private static bool Interlock(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime, DateTime now, GroupCommands commands)
    {
        var level = Level(steps, state);
        if (Reach(steps, state) <= level + 1)
        {
            return false;
        }

        var missing = level + 1;
        var step = steps[missing - 1];
        // Everything below the gap goes off at once, every time: also while a trip is being handled, e.g. after a
        // reconnect that shows the heaters on again. Sending it again is harmless.
        List<string> turnedOff = [];
        for (var p = missing + 1; p <= steps.Count; p++)
        {
            foreach (var relay in steps[p - 1].Relays.Where(r => state.IsOn(r.Channel)))
            {
                commands.Off |= ChannelMask.Of(relay.Channel);
                turnedOff.Add(NameOf(steps[p - 1], relay));
            }
        }

        var handling = runtime.Phase == GroupPhases.Stopping && runtime.PendingStep == missing && runtime.PendingRelayId is null && runtime.PendingOn;
        if (handling && now - (runtime.PendingSinceUtc ?? now) < ConfirmTimeout)
        {
            // This trip is being handled: its ON is on its way, and it is logged once.
            return true;
        }

        var off = step.Relays.Where(r => !state.IsOn(r.Channel)).ToList();
        commands.On |= Mask(off);
        runtime.Phase = GroupPhases.Stopping;
        runtime.TargetLevel = 0;
        runtime.NextStepAtUtc = null;
        runtime.AutoOffAtUtc = null;
        runtime.StopRelayIds = [];
        SetPending(runtime, missing, relayId: null, on: true, now);
        if (handling)
        {
            // Still broken after the timeout (e.g. a relay that doesn't answer): sent again, logged once.
            return true;
        }

        commands.Events.Add(new GroupEvent(
            EventActions.InterlockTrip,
            RelayIdOf(off),
            EventActors.System,
            JsonSerializer.Serialize(new { missing = NameOf(step, off), off = turnedOff })));
        return true;
    }

    // A step the board confirmed: when the next one may go.
    private static void Confirmed(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime, int level, int reach, DateTime now, GroupCommands commands)
    {
        if (runtime.Phase == GroupPhases.Starting)
        {
            runtime.NextStepAtUtc = level < steps.Count ? now.AddSeconds(steps[level].StartDelaySec) : null;
            return;
        }

        if (runtime.Phase != GroupPhases.Stopping || reach < 1 || StopDone(steps, state, runtime, reach))
        {
            return;
        }

        // The step below has gone off (or, after a trip, the missing relays are on): the lowest step still on runs on.
        var step = steps[reach - 1];
        var runOn = step.RunOnSec;
        runtime.NextStepAtUtc = now.AddSeconds(runOn);
        if (runOn > 0)
        {
            var running = GoingOff(step, state, runtime);
            commands.Events.Add(new GroupEvent(
                EventActions.RunOnStart,
                RelayIdOf(running),
                EventActors.System,
                JsonSerializer.Serialize(new { seconds = runOn, role = NameOf(step, running) })));
        }
    }

    // Whether a stop has gone as far as it should: down to its target, or, after one relay was turned off, until that
    // relay is off and nothing below it is on.
    private static bool StopDone(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime, int reach)
    {
        for (var p = 1; p <= steps.Count; p++)
        {
            var stopping = StopRelaysIn(steps[p - 1], runtime);
            if (stopping.Count > 0)
            {
                return reach < p || (reach == p && stopping.All(r => !state.IsOn(r.Channel)));
            }
        }

        return reach <= runtime.TargetLevel;
    }

    // Whether a relay that a stop of only some relays doesn't stop (one turned on again during it) has an ON on its way
    // that the board doesn't show yet. Bounded: an ON counts as on its way for a few seconds only.
    private static bool BroughtBackOnItsWay(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime, ChannelMask onOnItsWay)
    {
        return runtime.StopRelayIds.Count > 0
            && !onOnItsWay.IsEmpty
            && Relays(steps).Any(r => !runtime.StopRelayIds.Contains(r.Relay.RelayId) && onOnItsWay.Contains(r.Relay.Channel) && !state.IsOn(r.Relay.Channel));
    }

    // What of a step goes off next in a stop: the relays a person turned off, or every relay of it that is on.
    private static List<GroupRelayInfo> GoingOff(GroupStepInfo step, BoardState state, GroupRuntime runtime)
    {
        var stopping = StopRelaysIn(step, runtime);
        return [.. (stopping.Count > 0 ? stopping : step.Relays).Where(r => state.IsOn(r.Channel))];
    }

    // The position of the step a stop is turning off right now: its OFF sent and not yet confirmed, or due (no run-on
    // left to wait for). Null when none is.
    private static int? GoingOffStep(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime, DateTime now)
    {
        if (runtime.Phase != GroupPhases.Stopping)
        {
            return null;
        }

        if (runtime.PendingStep is { } pending)
        {
            return !runtime.PendingOn && pending >= 1 && pending <= steps.Count ? pending : null;
        }

        var reach = Reach(steps, state);
        var due = runtime.NextStepAtUtc is not { } next || now >= next;
        return due && reach >= 1 && !StopDone(steps, state, runtime, reach) ? reach : null;
    }

    // The position of the step whose relays were turned off one at a time and are stopping, or null.
    private static int? StoppingStep(IReadOnlyList<GroupStepInfo> steps, GroupRuntime runtime)
    {
        for (var p = 1; p <= steps.Count; p++)
        {
            if (StopRelaysIn(steps[p - 1], runtime).Count > 0)
            {
                return p;
            }
        }

        return null;
    }

    private static List<GroupRelayInfo> StopRelaysIn(GroupStepInfo step, GroupRuntime runtime)
    {
        return runtime.StopRelayIds.Count == 0 ? [] : [.. step.Relays.Where(r => runtime.StopRelayIds.Contains(r.RelayId))];
    }

    // The relays a command is waiting for: one relay, or every relay of its step. Null when the group no longer has them.
    private static (int Position, GroupStepInfo Step, IReadOnlyList<GroupRelayInfo> Relays)? Pending(IReadOnlyList<GroupStepInfo> steps, GroupRuntime runtime)
    {
        if (runtime.PendingStep is not { } position || position < 1 || position > steps.Count)
        {
            return null;
        }

        var step = steps[position - 1];
        if (runtime.PendingRelayId is not { } id)
        {
            // A stop's OFF for the relays a person turned off waits for those only.
            var stopping = !runtime.PendingOn && runtime.Phase == GroupPhases.Stopping ? StopRelaysIn(step, runtime) : [];
            return (position, step, stopping.Count > 0 ? stopping : step.Relays);
        }

        return step.Relays.FirstOrDefault(r => r.RelayId == id) is { } relay ? (position, step, [relay]) : null;
    }

    // Several relays go by their step's name ("Fans"), one by its own.
    private static string NameOf(GroupStepInfo step, List<GroupRelayInfo> relays)
    {
        return relays.Count == 1 ? NameOf(step, relays[0]) : step.Name;
    }

    // An event about one relay names it; one about several names none (the event names their step).
    private static int? RelayIdOf(List<GroupRelayInfo> relays)
    {
        return relays.Count == 1 ? relays[0].RelayId : null;
    }

    private static int IndexOf(IReadOnlyList<GroupStepInfo> steps, GroupStepInfo step)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (ReferenceEquals(steps[i], step))
            {
                return i;
            }
        }

        throw new ArgumentException("The step isn't in the group.", nameof(step));
    }

    // A relay of a stop's step brought back while the others still go off: its ON when it is (or may be) off, and the
    // run is the person's again.
    private static void BackOn(GroupRelayInfo relay, BoardState onState, string actor, int? maxOnMinutes, DateTime now, GroupRuntime runtime, GroupCommands commands)
    {
        if (!onState.IsOn(relay.Channel))
        {
            commands.On |= ChannelMask.Of(relay.Channel);
            commands.Events.Add(new GroupEvent(EventActions.On, relay.RelayId, actor, null));
        }

        KeepsRunning(runtime, actor, maxOnMinutes, now);
    }

    // What a stop goes by: the board's state with the relays whose ON is on its way as on. Such a relay (e.g. one turned
    // on again while the others of its step stopped) is turned off as well, after its ON and from the bottom, so nothing
    // is left on once the stop is done and nothing below it outlives the step above. A stop of some relays leaves one
    // turned on again in their step running. It counts the relays it stops, so the OFF of one turned off while its ON
    // was on its way is done only once that ON is no longer on its way. Of the relays below, it counts only those of the
    // step below theirs whose OFF it waits for (sent with the others below when one relay was turned off), so that OFF is
    // done only once their ON is no longer on its way. Relays further below had their OFF sent at the same time, after
    // their ON, and the board takes its commands in order: they don't hold up the run-on of the step above, nor set its
    // time.
    private static BoardState StopState(IReadOnlyList<GroupStepInfo> steps, BoardState state, GroupRuntime runtime, ChannelMask onOnItsWay)
    {
        if (runtime.Phase != GroupPhases.Stopping || onOnItsWay.IsEmpty)
        {
            return state;
        }

        if (runtime.StopRelayIds.Count == 0)
        {
            return With(state, onOnItsWay);
        }

        var waited = !runtime.PendingOn && runtime.PendingRelayId is null && runtime.PendingStep is { } pending
            && StoppingStep(steps, runtime) is { } stopping && pending > stopping && pending <= steps.Count
            ? Mask(steps[pending - 1].Relays)
            : ChannelMask.None;
        var stopped = Mask(Relays(steps).Select(r => r.Relay).Where(r => runtime.StopRelayIds.Contains(r.RelayId)));
        return With(state, onOnItsWay & (waited | stopped));
    }

    // The board's state with these relays on: an ON for them is on its way.
    private static BoardState With(BoardState state, ChannelMask on)
    {
        return on.IsEmpty ? state : state with { Relays = state.Relays | on };
    }

    // The board's state with these relays off: an OFF for them is on its way.
    private static BoardState Without(BoardState state, ChannelMask off)
    {
        return off.IsEmpty ? state : state with { Relays = state.Relays.Except(off) };
    }

    private static ChannelMask Mask(IEnumerable<GroupRelayInfo> relays)
    {
        return ChannelMask.Of(relays.Select(r => r.Channel));
    }

    // A stop called off by a person turning a relay on: the run is theirs again, with the group's auto-off when the stop
    // had taken it away, so nothing stays on for ever by accident.
    private static void KeepsRunning(GroupRuntime runtime, string actor, int? maxOnMinutes, DateTime now)
    {
        runtime.TurnedOnBy = actor;
        runtime.OnSinceUtc ??= now;
        if (runtime.AutoOffAtUtc is null && maxOnMinutes is { } minutes)
        {
            runtime.AutoOffAtUtc = now.AddMinutes(minutes);
        }
    }

    private static void BeginStop(GroupRuntime runtime)
    {
        runtime.Phase = GroupPhases.Stopping;
        runtime.TargetLevel = 0;
        runtime.NextStepAtUtc = null;
        runtime.AutoOffAtUtc = null;
        runtime.StopRelayIds = [];
        ClearPending(runtime);
    }

    private static void SetPending(GroupRuntime runtime, int step, int? relayId, bool on, DateTime now)
    {
        runtime.PendingStep = step;
        runtime.PendingRelayId = relayId;
        runtime.PendingOn = on;
        runtime.PendingSinceUtc = now;
    }

    private static void ClearPending(GroupRuntime runtime)
    {
        runtime.PendingStep = null;
        runtime.PendingRelayId = null;
        runtime.PendingOn = false;
        runtime.PendingSinceUtc = null;
    }

    private static void ClearRun(GroupRuntime runtime)
    {
        runtime.OnSinceUtc = null;
        runtime.AutoOffAtUtc = null;
        runtime.TurnedOnBy = null;
    }
}
