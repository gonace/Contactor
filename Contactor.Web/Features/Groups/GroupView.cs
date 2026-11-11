using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;

namespace Contactor.Web.Features.Groups;

/// <summary>A relay of a group, for the screens.</summary>
public sealed record GroupMemberView(int RelayId, int Position, string RoleName, int StartDelaySec, int RunOnSec);

/// <summary>A group as the screens show it: its setup and where its sequence is. Whether relays are on comes from the board.</summary>
public sealed record GroupView(
    int Id,
    string Name,
    string Icon,
    int? MaxOnMinutes,
    IReadOnlyList<GroupMemberView> Members,
    GroupRuntime Runtime)
{
    /// <summary>The members as the planner sees them.</summary>
    public IReadOnlyList<GroupMemberInfo> Infos =>
        [.. Members.Select(m => new GroupMemberInfo(m.Position, m.RelayId, m.RoleName, m.StartDelaySec, m.RunOnSec))];

    /// <summary>"RELAY 1 → 2".</summary>
    public string Chain => "RELAY " + string.Join(" → ", Members.Select(m => m.RelayId));

    /// <summary>The relays of the group, as a channel mask.</summary>
    public ChannelMask Mask => ChannelMask.Of(Members.Select(m => m.RelayId));

    /// <summary>
    /// Whether the group counts as on: starting, or any relay on and not on its way down. The big button then reads
    /// ON, and a press stops the group; during a run-on it reads OFF, and a press starts it again.
    /// </summary>
    public bool IsOn(BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Runtime.Phase == GroupPhases.Starting
            || (Runtime.Phase != GroupPhases.Stopping && GroupPlanner.AnyOn(Infos, state));
    }
}

/// <summary>The status line under a group's name.</summary>
public static class GroupStatusText
{
    /// <param name="toLocal">From UTC to local time.</param>
    /// <param name="nameOf">"you" for the viewer, otherwise a person's name, "schedule" or "auto-off".</param>
    /// <param name="outlook">What the group's schedules say: when a run ends, or when the next one starts.</param>
    public static string For(
        GroupView group,
        BoardState state,
        DateTime nowUtc,
        Func<DateTime, DateTime> toLocal,
        Func<string?, string?> nameOf,
        RelayOutlook? outlook = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(toLocal);
        ArgumentNullException.ThrowIfNull(nameOf);
        var runtime = group.Runtime;
        var level = GroupPlanner.Level(group.Infos, state);
        var n = group.Members.Count;
        if (n == 0)
        {
            return "Off";
        }

        if (runtime.Phase == GroupPhases.Starting)
        {
            return $"Starting · step {Math.Clamp(level + 1, 1, n)} of {n}";
        }

        if (runtime.Phase == GroupPhases.Stopping)
        {
            if (level >= 1 && runtime.NextStepAtUtc is { } next && next > nowUtc)
            {
                return $"Stopping · {group.Members[level - 1].RoleName} keeps running for {Remaining(next - nowUtc)}";
            }

            return "Stopping";
        }

        if (!GroupPlanner.AnyOn(group.Infos, state))
        {
            return outlook?.NextOnUtc is { } nextOn
                ? $"Schedule: on {RelayStatusText.When(toLocal(nextOn), toLocal(nowUtc))}"
                : "Off";
        }

        // A relay is on while the one above it is off, e.g. switched at the board: the interlock turns it off.
        if (level == 0)
        {
            return "Out of order · turning off";
        }

        // A group the schedule started goes off when its run ends.
        var offAtUtc = runtime.TurnedOnBy == EventActors.Schedule ? outlook?.RunningUntilUtc : runtime.AutoOffAtUtc;
        var nowLocal = toLocal(nowUtc);
        var text = RelayStatusText.For(
            true,
            runtime.OnSinceUtc is { } since ? toLocal(since) : null,
            nameOf(runtime.TurnedOnBy),
            offAtUtc is { } off ? toLocal(off) : null,
            nowLocal);

        // Only some of it runs, e.g. the fan alone.
        if (level < n)
        {
            text += " · only " + string.Join(", ", group.Members.Take(level).Select(m => m.RoleName));
        }

        return text;
    }

    /// <summary>"4 min", or "40 s" under a minute; rounded up, so it never reads 0.</summary>
    public static string Remaining(TimeSpan left)
    {
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        return seconds < 60 ? $"{Math.Max(seconds, 1)} s" : $"{(seconds + 59) / 60} min";
    }
}

/// <summary>What a group will do, in plain words, for "Review and create".</summary>
public static class GroupPlanText
{
    /// <summary>"Fan → wait 3 s → Heat".</summary>
    public static string Start(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Concat(members.Select((m, i) => i == 0 ? m.RoleName : $" → wait {EventText.Seconds(m.StartDelaySec)} → {m.RoleName}"));
    }

    /// <summary>"Heat off → Fan keeps running for 5 min, then off": from the bottom, each with its run-on.</summary>
    public static string Stop(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(" → ", members.Reverse().Select((m, i) => i > 0 && m.RunOnSec > 0
            ? $"{m.RoleName} keeps running for {EventText.Seconds(m.RunOnSec)}, then off"
            : $"{m.RoleName} off"));
    }

    /// <summary>"Heat can only be on while Fan is on."</summary>
    public static string Interlock(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(" ", members.Skip(1).Select((m, i) => $"{m.RoleName} can only be on while {members[i].RoleName} is on."));
    }
}
