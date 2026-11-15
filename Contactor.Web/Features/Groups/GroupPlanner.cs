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
    public static void Plan(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        DateTime now,
        GroupCommands commands)
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

        bool On(GroupRelayInfo relay) => state.IsOn(relay.Channel);
        var level = Level(steps, state);
        var reach = Reach(steps, state);

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
            else if (pending.Relays.All(r => On(r) == runtime.PendingOn))
            {
                ClearPending(runtime);
                Confirmed(steps, state, runtime, level, reach, now, commands);
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
                    Confirmed(steps, state, runtime, level, reach, now, commands);
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
            if (StopDone(steps, state, runtime, reach))
            {
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
                var going = GoingOff(step, state, runtime);
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
            Plan(steps, state, runtime, now, commands);
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
        GroupCommands commands)
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
            commands.On |= Mask(pending.Relays);
            SetPending(runtime, pending.Position, overtakenRelayId, on: true, now);
        }

        if (runtime.OnSinceUtc is null || !AnyOn(steps, state))
        {
            runtime.OnSinceUtc = now;
        }

        runtime.TurnedOnBy = actor;
        runtime.AutoOffAtUtc = maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
        commands.Events.Add(new GroupEvent(EventActions.GroupOn, null, actor, Chain(steps)));
        Plan(steps, state, runtime, now, commands);
        return true;
    }

    /// <summary>Stops the whole group, from the bottom, with run-on times. Returns false when there is nothing to stop.</summary>
    public static bool Stop(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        string actor,
        DateTime now,
        GroupCommands commands)
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
            runtime.TargetLevel = 0;
            runtime.StopRelayIds = [];
            runtime.AutoOffAtUtc = null;
            commands.Events.Add(new GroupEvent(EventActions.GroupOff, null, actor, null));
            Plan(steps, state, runtime, now, commands);
            return true;
        }

        // An ON on its way (a start, or one relay turned on) is overtaken: it gets an OFF, and the stop waits for the
        // board to confirm it before the step above runs on, as in any stop.
        var overtaken = runtime.PendingOn ? Pending(steps, runtime) : null;
        var overtakenRelayId = runtime.PendingRelayId;
        if (overtaken is null && !AnyOn(steps, state) && runtime.Phase == GroupPhases.Idle)
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

        Plan(steps, state, runtime, now, commands);
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
        GroupCommands commands)
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

        if (NeedsOn(steps, state, position) is { } needs)
        {
            return new MemberRefusal(needs);
        }

        // A trip's run-on relays on their way: the trip's stop isn't called off, and nothing more is turned on.
        if (runtime.Phase == GroupPhases.Stopping && runtime.PendingOn)
        {
            return null;
        }

        if (state.IsOn(relay.Channel))
        {
            // E.g. the fan during its run-on: it stays on, and the stop is called off. A start going on is left alone.
            if (runtime.Phase != GroupPhases.Stopping)
            {
                return null;
            }

            runtime.Phase = GroupPhases.Idle;
            runtime.TargetLevel = Math.Max(Level(steps, state), place);
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

        commands.On |= ChannelMask.Of(relay.Channel);
        runtime.Phase = GroupPhases.Idle;
        runtime.TargetLevel = Math.Max(Level(steps, state), place);
        runtime.NextStepAtUtc = null;
        runtime.StopRelayIds = [];
        SetPending(runtime, place, relay.RelayId, on: true, now);
        if (!AnyOn(steps, state) || runtime.OnSinceUtc is null)
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
    /// already.
    /// </summary>
    /// <param name="position">The relay's position in the group's start order, from 1.</param>
    public static void MemberOff(
        IReadOnlyList<GroupStepInfo> steps,
        BoardState state,
        GroupRuntime runtime,
        int position,
        string actor,
        DateTime now,
        GroupCommands commands)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        var (step, relay) = RelayAt(steps, position);

        // The interlock first, as on every status; a trip stops the whole group, which this relay's OFF then keeps.
        Interlock(steps, state, runtime, now, commands);
        if (!state.IsOn(relay.Channel))
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
            }

            return;
        }

        runtime.Phase = GroupPhases.Stopping;
        runtime.TargetLevel = place - 1;
        runtime.StopRelayIds = [relay.RelayId];
        runtime.NextStepAtUtc = null;
        ClearPending(runtime);
        var below = Mask(steps.Skip(place).SelectMany(s => s.Relays).Where(r => state.IsOn(r.Channel)));
        commands.Events.Add(new GroupEvent(EventActions.Off, relay.RelayId, actor, null));
        if (!below.IsEmpty)
        {
            // The ones below at once; this one runs on once they are confirmed off.
            commands.Off |= below;
            SetPending(runtime, place + 1, relayId: null, on: false, now);
            return;
        }

        Plan(steps, state, runtime, now, commands);
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

    // What of a step goes off next in a stop: the relays a person turned off, or every relay of it that is on.
    private static List<GroupRelayInfo> GoingOff(GroupStepInfo step, BoardState state, GroupRuntime runtime)
    {
        var stopping = StopRelaysIn(step, runtime);
        return [.. (stopping.Count > 0 ? stopping : step.Relays).Where(r => state.IsOn(r.Channel))];
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
