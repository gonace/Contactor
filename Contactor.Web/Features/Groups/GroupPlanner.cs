using System.Text.Json;
using Contactor.Vm201;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Groups;

/// <summary>A relay in a group, as the planner sees it.</summary>
/// <param name="Position">1 starts first and stops last.</param>
public sealed record GroupMemberInfo(int Position, int RelayId, string RoleName, int StartDelaySec, int RunOnSec);

/// <summary>An event the planner wants logged, with the group's id added by the caller.</summary>
public sealed record GroupEvent(string Action, int? RelayId, string Actor, string? Details);

/// <summary>What to send to the board and log after a planning step. OFF goes first, so a relay never runs alone below a gap.</summary>
public sealed class GroupCommands
{
    public byte Off { get; set; }

    public byte On { get; set; }

    public List<GroupEvent> Events { get; } = [];

    public bool IsEmpty => Off == 0 && On == 0 && Events.Count == 0;
}

/// <summary>
/// The rules of a relay group, apart from the board and the database: given the members in start order, which of
/// them the board says are on, the group's <see cref="GroupRuntime"/> and the time, it decides the next commands and
/// updates the runtime. <see cref="GroupSequencer"/> runs it on every status from the board and every second.
/// <list type="bullet">
/// <item>Start: from the top; the next relay only once the board confirms the one before is on and its start delay has
/// passed. No confirmation within <see cref="ConfirmTimeout"/>: the start stops, what runs keeps running.</item>
/// <item>Stop: from the bottom; the lowest relay goes off at once, each one above it after its run-on time.</item>
/// <item>Interlock: a relay may only be on while every relay above it is. Checked first, every time, whatever turned
/// the relays on: a broken interlock turns off everything below the gap at once and starts the missing relay, so it
/// can run on and then stop as in a normal stop.</item>
/// </list>
/// </summary>
public static class GroupPlanner
{
    /// <summary>How long the board has to confirm a command before the step counts as failed.</summary>
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many relays from the top are on without a gap.</summary>
    public static int Level(IReadOnlyList<GroupMemberInfo> members, Vm201State state)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(state);
        var level = 0;
        while (level < members.Count && state.IsOn(members[level].RelayId))
        {
            level++;
        }

        return level;
    }

    /// <summary>Whether any relay of the group is on.</summary>
    public static bool AnyOn(IReadOnlyList<GroupMemberInfo> members, Vm201State state)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(state);
        return members.Any(m => state.IsOn(m.RelayId));
    }

    /// <summary>The next step for one group. Updates <paramref name="runtime"/> and adds to <paramref name="commands"/>.</summary>
    public static void Plan(
        IReadOnlyList<GroupMemberInfo> members,
        Vm201State state,
        GroupRuntime runtime,
        DateTime now,
        GroupCommands commands)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        var n = members.Count;
        if (n == 0)
        {
            return;
        }

        bool On(int position) => state.IsOn(members[position - 1].RelayId);
        byte Mask(int position) => Vm201Channel.Mask(members[position - 1].RelayId);
        var level = Level(members, state);

        // 1. The interlock, before anything else.
        var highest = 0;
        for (var p = n; p >= 1; p--)
        {
            if (On(p))
            {
                highest = p;
                break;
            }
        }

        if (highest > level)
        {
            var missing = level + 1;
            var handling = runtime.Phase == GroupPhases.Stopping && runtime.PendingPosition == missing && runtime.PendingOn;
            if (handling && now - (runtime.PendingSinceUtc ?? now) < ConfirmTimeout)
            {
                // This trip is being handled; the board hasn't reported it yet.
                return;
            }

            List<string> turnedOff = [];
            for (var p = missing + 1; p <= n; p++)
            {
                if (On(p))
                {
                    commands.Off |= Mask(p);
                    turnedOff.Add(members[p - 1].RoleName);
                }
            }

            commands.On |= Mask(missing);
            runtime.Phase = GroupPhases.Stopping;
            runtime.TargetLevel = 0;
            runtime.NextStepAtUtc = null;
            runtime.AutoOffAtUtc = null;
            SetPending(runtime, missing, on: true, now);
            if (handling)
            {
                // Still broken after the timeout (e.g. a relay that doesn't answer): sent again, logged once.
                return;
            }

            commands.Events.Add(new GroupEvent(
                EventActions.InterlockTrip,
                members[missing - 1].RelayId,
                EventActors.System,
                JsonSerializer.Serialize(new { missing = members[missing - 1].RoleName, off = turnedOff })));
            return;
        }

        // 2. A command the board hasn't confirmed yet.
        if (runtime.PendingPosition is { } pending && pending >= 1 && pending <= n)
        {
            if (On(pending) == runtime.PendingOn)
            {
                ClearPending(runtime);
                Confirmed(members, runtime, level, now, commands);
            }
            else if (now - (runtime.PendingSinceUtc ?? now) >= ConfirmTimeout)
            {
                if (runtime.PendingOn && runtime.Phase != GroupPhases.Idle)
                {
                    // A relay that didn't come on: a step of a start, or the relay a trip started to run on. Never
                    // sent again: what runs keeps running, and a stop carries on without it.
                    commands.Events.Add(new GroupEvent(
                        EventActions.GroupStartFailed,
                        members[pending - 1].RelayId,
                        EventActors.System,
                        JsonSerializer.Serialize(new { step = pending, role = members[pending - 1].RoleName })));
                    ClearPending(runtime);
                    runtime.NextStepAtUtc = null;
                    if (runtime.Phase == GroupPhases.Starting)
                    {
                        runtime.Phase = GroupPhases.Idle;
                        runtime.TargetLevel = level;
                        return;
                    }

                    // The stop carries on: the lowest relay still on runs on first, as after any confirmed step.
                    Confirmed(members, runtime, level, now, commands);
                }
                else if (runtime.Phase == GroupPhases.Idle)
                {
                    ClearPending(runtime);
                }
                else
                {
                    // A stop must happen: send it again.
                    commands.Off |= Mask(pending);
                    runtime.PendingSinceUtc = now;
                    return;
                }
            }
            else
            {
                return;
            }
        }
        else if (runtime.PendingPosition is not null)
        {
            // A position the group no longer has.
            ClearPending(runtime);
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
                commands.On |= Mask(level + 1);
                SetPending(runtime, level + 1, on: true, now);
            }

            return;
        }

        if (runtime.Phase == GroupPhases.Stopping)
        {
            if (level <= runtime.TargetLevel)
            {
                runtime.Phase = GroupPhases.Idle;
                runtime.NextStepAtUtc = null;
                if (level == 0)
                {
                    ClearRun(runtime);
                }

                return;
            }

            if (runtime.NextStepAtUtc is null || now >= runtime.NextStepAtUtc)
            {
                var ranOn = runtime.NextStepAtUtc is not null && level < n && members[level - 1].RunOnSec > 0;
                commands.Off |= Mask(level);
                SetPending(runtime, level, on: false, now);
                runtime.NextStepAtUtc = null;
                if (ranOn)
                {
                    commands.Events.Add(new GroupEvent(
                        EventActions.RunOnEnd,
                        members[level - 1].RelayId,
                        EventActors.System,
                        JsonSerializer.Serialize(new { role = members[level - 1].RoleName })));
                }
            }

            return;
        }

        // Idle.
        if (level == 0)
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
            Plan(members, state, runtime, now, commands);
        }
    }

    /// <summary>
    /// Starts the whole group, or carries on from where it is (e.g. during a run-on: nothing that runs is restarted).
    /// Returns false when it is already starting.
    /// </summary>
    public static bool Start(
        IReadOnlyList<GroupMemberInfo> members,
        Vm201State state,
        GroupRuntime runtime,
        string actor,
        int? maxOnMinutes,
        DateTime now,
        GroupCommands commands)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        if (runtime.Phase == GroupPhases.Starting)
        {
            return false;
        }

        // An OFF on its way (the group was stopping) is overtaken by an ON, which is then waited for.
        var overtaken = runtime.PendingPosition is { } pending && !runtime.PendingOn && pending >= 1 && pending <= members.Count
            ? pending
            : (int?)null;
        runtime.Phase = GroupPhases.Starting;
        runtime.TargetLevel = members.Count;
        runtime.NextStepAtUtc = null;
        ClearPending(runtime);
        if (overtaken is { } position)
        {
            commands.On |= Vm201Channel.Mask(members[position - 1].RelayId);
            SetPending(runtime, position, on: true, now);
        }

        if (runtime.OnSinceUtc is null || !AnyOn(members, state))
        {
            runtime.OnSinceUtc = now;
        }

        runtime.TurnedOnBy = actor;
        runtime.AutoOffAtUtc = maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
        commands.Events.Add(new GroupEvent(EventActions.GroupOn, null, actor, Chain(members)));
        Plan(members, state, runtime, now, commands);
        return true;
    }

    /// <summary>Stops the whole group, from the bottom, with run-on times. Returns false when there is nothing to stop.</summary>
    public static bool Stop(
        IReadOnlyList<GroupMemberInfo> members,
        Vm201State state,
        GroupRuntime runtime,
        string actor,
        DateTime now,
        GroupCommands commands)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        // Already stopping (a trip's run-on relay on its way included): that stop carries on.
        if (runtime.Phase == GroupPhases.Stopping && runtime.TargetLevel == 0)
        {
            return false;
        }

        // An ON on its way (a start, or one relay turned on) is overtaken: it gets an OFF, and the stop waits for the
        // board to confirm it before the relay above runs on, as in any stop.
        var overtaken = runtime.PendingPosition is { } pending && runtime.PendingOn && pending >= 1 && pending <= members.Count
            ? pending
            : (int?)null;
        if (overtaken is null && !AnyOn(members, state) && runtime.Phase == GroupPhases.Idle)
        {
            return false;
        }

        BeginStop(runtime);
        commands.Events.Add(new GroupEvent(EventActions.GroupOff, null, actor, null));
        if (overtaken is { } position)
        {
            commands.Off |= Vm201Channel.Mask(members[position - 1].RelayId);
            SetPending(runtime, position, on: false, now);
            return true;
        }

        Plan(members, state, runtime, now, commands);
        return true;
    }

    /// <summary>
    /// Turns one relay on, within the interlock: only when the relay above it is on. Returns why not, in Swedish, or
    /// null when done.
    /// </summary>
    public static string? MemberOn(
        IReadOnlyList<GroupMemberInfo> members,
        Vm201State state,
        GroupRuntime runtime,
        int position,
        string actor,
        int? maxOnMinutes,
        DateTime now,
        GroupCommands commands)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, members.Count);
        var member = members[position - 1];
        if (position > 1 && !state.IsOn(members[position - 2].RelayId))
        {
            return $"Kräver att {members[position - 2].RoleName} är på.";
        }

        if (state.IsOn(member.RelayId))
        {
            // E.g. the fan during its run-on: it stays on, and the stop is called off. A start going on is left alone.
            if (runtime.Phase != GroupPhases.Stopping)
            {
                return null;
            }

            runtime.Phase = GroupPhases.Idle;
            runtime.TargetLevel = Math.Max(Level(members, state), position);
            runtime.NextStepAtUtc = null;
            ClearPending(runtime);
            return null;
        }

        commands.On |= Vm201Channel.Mask(member.RelayId);
        runtime.Phase = GroupPhases.Idle;
        runtime.TargetLevel = Math.Max(Level(members, state), position);
        runtime.NextStepAtUtc = null;
        SetPending(runtime, position, on: true, now);
        if (!AnyOn(members, state) || runtime.OnSinceUtc is null)
        {
            runtime.OnSinceUtc = now;
            runtime.AutoOffAtUtc = maxOnMinutes is { } minutes ? now.AddMinutes(minutes) : null;
        }

        runtime.TurnedOnBy = actor;
        commands.Events.Add(new GroupEvent(EventActions.On, member.RelayId, actor, null));
        return null;
    }

    /// <summary>
    /// Turns one relay off: the relays below it go off at once, and this one runs on for its run-on time first if any
    /// were on. Nothing happens when it is off already.
    /// </summary>
    public static void MemberOff(
        IReadOnlyList<GroupMemberInfo> members,
        Vm201State state,
        GroupRuntime runtime,
        int position,
        string actor,
        DateTime now,
        GroupCommands commands)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, members.Count);
        var member = members[position - 1];
        if (!state.IsOn(member.RelayId))
        {
            return;
        }

        runtime.Phase = GroupPhases.Stopping;
        runtime.TargetLevel = position - 1;
        runtime.NextStepAtUtc = null;
        ClearPending(runtime);
        byte below = 0;
        for (var p = position + 1; p <= members.Count; p++)
        {
            if (state.IsOn(members[p - 1].RelayId))
            {
                below |= Vm201Channel.Mask(members[p - 1].RelayId);
            }
        }

        commands.Events.Add(new GroupEvent(EventActions.Off, member.RelayId, actor, null));
        if (below != 0)
        {
            // The ones below at once; this one runs on once they are confirmed off.
            commands.Off |= below;
            SetPending(runtime, position + 1, on: false, now);
            return;
        }

        Plan(members, state, runtime, now, commands);
    }

    /// <summary>"Fläkt → 3 s → Värme", for the log.</summary>
    public static string Chain(IReadOnlyList<GroupMemberInfo> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return JsonSerializer.Serialize(new
        {
            steps = members.Select(m => m.RoleName).ToArray(),
            delays = members.Skip(1).Select(m => m.StartDelaySec).ToArray(),
        });
    }

    // A step the board confirmed: when the next one may go.
    private static void Confirmed(IReadOnlyList<GroupMemberInfo> members, GroupRuntime runtime, int level, DateTime now, GroupCommands commands)
    {
        if (runtime.Phase == GroupPhases.Starting)
        {
            runtime.NextStepAtUtc = level < members.Count ? now.AddSeconds(members[level].StartDelaySec) : null;
            return;
        }

        if (runtime.Phase != GroupPhases.Stopping || level < 1 || level <= runtime.TargetLevel)
        {
            return;
        }

        // The relay below has gone off (or, after a trip, the missing one is on): the lowest one still on runs on.
        var runOn = members[level - 1].RunOnSec;
        runtime.NextStepAtUtc = now.AddSeconds(runOn);
        if (runOn > 0)
        {
            commands.Events.Add(new GroupEvent(
                EventActions.RunOnStart,
                members[level - 1].RelayId,
                EventActors.System,
                JsonSerializer.Serialize(new { seconds = runOn, role = members[level - 1].RoleName })));
        }
    }

    private static void BeginStop(GroupRuntime runtime)
    {
        runtime.Phase = GroupPhases.Stopping;
        runtime.TargetLevel = 0;
        runtime.NextStepAtUtc = null;
        runtime.AutoOffAtUtc = null;
        ClearPending(runtime);
    }

    private static void SetPending(GroupRuntime runtime, int position, bool on, DateTime now)
    {
        runtime.PendingPosition = position;
        runtime.PendingOn = on;
        runtime.PendingSinceUtc = now;
    }

    private static void ClearPending(GroupRuntime runtime)
    {
        runtime.PendingPosition = null;
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
