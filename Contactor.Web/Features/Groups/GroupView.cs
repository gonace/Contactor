using Contactor.Boards;
using Contactor.Web.Features.Events;
using Contactor.Web.Features.Relays;
using Contactor.Web.Features.Schedules;
using Contactor.Web.Persistence;
using Contactor.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Contactor.Web.Features.Groups;

/// <summary>A relay of a group, for the screens.</summary>
/// <param name="RelayId">The relay, for links and the database.</param>
/// <param name="Channel">Its channel on the board: what the board's state goes by, and the number the screens show.</param>
/// <param name="Position">Its place in the group's start order, from 1: the steps' relays one after another.</param>
/// <param name="Step">Its step's place, from 1.</param>
/// <param name="RoleName">Its name in the group: its step's when it is the step's only relay, otherwise its own.</param>
/// <param name="StartDelaySec">Its step's wait before starting.</param>
/// <param name="RunOnSec">Its step's run-on.</param>
public sealed record GroupMemberView(int RelayId, int Channel, int Position, int Step, string RoleName, int StartDelaySec, int RunOnSec);

/// <summary>A group's steps as the planner and the screens see them.</summary>
public static class GroupSteps
{
    /// <summary>The steps in start order, each with its relays in order. The group comes with its steps, their members and their relays.</summary>
    public static IReadOnlyList<GroupStepInfo> Of(RelayGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return [.. group.Steps.OrderBy(s => s.Position).Select(s => new GroupStepInfo(
            s.Name,
            s.StartDelaySec,
            s.RunOnSec,
            [.. s.Members.OrderBy(m => m.Order).Select(m => new GroupRelayInfo(
                m.RelayId,
                m.Relay?.Channel ?? throw new InvalidOperationException("A group's members are read with their relays."),
                m.Relay.Name))]))];
    }
}

/// <summary>A group as the screens show it: its setup and where its sequence is. Whether relays are on comes from the board.</summary>
/// <param name="Steps">Its steps in start order, as the planner sees them.</param>
public sealed record GroupView(
    int Id,
    string Name,
    string Icon,
    int? MaxOnMinutes,
    IReadOnlyList<GroupStepInfo> Steps,
    GroupRuntime Runtime)
{
    /// <summary>The group's board: all its relays are on it.</summary>
    public int BoardId { get; init; }

    /// <summary>Every relay of the group, in start order.</summary>
    public IReadOnlyList<GroupMemberView> Members =>
        [.. Steps.SelectMany((step, index) => step.Relays.Select(relay => (step, index, relay)))
            .Select((m, i) => new GroupMemberView(
                m.relay.RelayId, m.relay.Channel, i + 1, m.index + 1, GroupPlanner.NameOf(m.step, m.relay), m.step.StartDelaySec, m.step.RunOnSec))];

    /// <summary>
    /// The channel of the first relay of the group the board doesn't have (left from a board with more relays), or
    /// null. Such a group can't run: its relays are kept off until it is changed or deleted.
    /// </summary>
    public int? MissingRelay(int relayCount)
    {
        return Members.Where(m => m.Channel > relayCount).Select(m => (int?)m.Channel).FirstOrDefault();
    }

    /// <summary>"1 + 2 → 3": the relays' channels, in start order, a step's joined by "+".</summary>
    public string Channels => string.Join(" → ", Steps.Select(s => string.Join(" + ", s.Relays.Select(r => r.Channel))));

    /// <summary>The relays of the group, as a channel mask.</summary>
    public ChannelMask Mask => ChannelMask.Of(Members.Select(m => m.Channel));

    /// <summary>
    /// Whether the group counts as on: starting, or any relay on and not on its way down. The big button then reads
    /// ON, and a press stops the group; during a run-on it reads OFF, and a press starts it again.
    /// </summary>
    public bool IsOn(BoardState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Runtime.Phase == GroupPhases.Starting
            || (Runtime.Phase != GroupPhases.Stopping && GroupPlanner.AnyOn(Steps, state));
    }
}

/// <summary>A group's status in words, in the app's language: the line under its name and each relay's note.</summary>
public sealed class GroupStatusText
{
    private readonly IStringLocalizer<Texts> _texts;
    private readonly RelayStatusText _relayStatus;
    private readonly EventText _lengths;

    public GroupStatusText(IStringLocalizer<Texts> texts, RelayStatusText relayStatus, EventText lengths)
    {
        _texts = texts;
        _relayStatus = relayStatus;
        _lengths = lengths;
    }

    /// <param name="toLocal">From UTC to local time.</param>
    /// <param name="nameOf">"you" for the viewer, otherwise a person's name, "schedule" or "auto-off", in words.</param>
    /// <param name="outlook">What the group's schedules say: when a run ends, or when the next one starts.</param>
    public string For(
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
        var steps = group.Steps;
        var level = GroupPlanner.Level(steps, state);
        var reach = GroupPlanner.Reach(steps, state);
        var n = steps.Count;
        if (n == 0)
        {
            return _texts["Groups.Status.Off"];
        }

        if (runtime.Phase == GroupPhases.Starting)
        {
            // Waiting out the start delay before the next step: count it down.
            if (level < n && runtime.NextStepAtUtc is { } nextStart && nextStart > nowUtc)
            {
                return _texts["Groups.Status.StartingIn", steps[level].Name, Remaining(nextStart - nowUtc)];
            }

            return _texts["Groups.Status.StartingStep", Math.Clamp(level + 1, 1, n), n];
        }

        if (runtime.Phase == GroupPhases.Stopping)
        {
            var running = GroupPlanner.RunningOn(steps, state, runtime);
            if (running.Count > 0 && runtime.NextStepAtUtc is { } next && next > nowUtc)
            {
                var name = running.Count == 1 ? GroupPlanner.NameOf(steps[reach - 1], running[0]) : steps[reach - 1].Name;
                return _texts["Groups.Status.StoppingRunOn", name, Remaining(next - nowUtc)];
            }

            return _texts["Groups.Status.Stopping"];
        }

        if (reach == 0)
        {
            return outlook?.NextOnUtc is { } nextOn
                ? _texts["Groups.Status.ScheduleOn", _relayStatus.When(toLocal(nextOn), toLocal(nowUtc))]
                : _texts["Groups.Status.Off"];
        }

        // A relay is on while a relay of a step above it is off, e.g. switched at the board: the interlock turns it off.
        if (reach > level + 1)
        {
            return _texts["Groups.Status.OutOfOrder"];
        }

        // A group the schedule started goes off when its run ends.
        var offAtUtc = runtime.TurnedOnBy == EventActors.Schedule ? outlook?.RunningUntilUtc : runtime.AutoOffAtUtc;
        var nowLocal = toLocal(nowUtc);
        var text = _relayStatus.For(
            true,
            runtime.OnSinceUtc is { } since ? toLocal(since) : null,
            nameOf(runtime.TurnedOnBy),
            offAtUtc is { } off ? toLocal(off) : null,
            nowLocal);

        // Only some of it runs, e.g. the fan alone: the steps that are all on by their names, and of a step that is
        // partly on, the relays that are.
        if (level >= n)
        {
            return text;
        }

        var names = steps.Take(level).Select(s => s.Name)
            .Concat(steps[level].Relays.Where(r => state.IsOn(r.Channel)).Select(r => GroupPlanner.NameOf(steps[level], r)));
        return _texts["Groups.Status.Only", text, string.Join(_texts["Groups.List.Separator"], names)];
    }

    /// <summary>
    /// The state of the step at <paramref name="index"/> (from 0, in start order) on the Group screen: "Keeps running for
    /// 4 min 59 s", "On", "Only Fan 1 on", "Starts in 3 s", "Needs Fans to be on", "Off · starts first" or "Off".
    /// </summary>
    public string StepNote(GroupView group, BoardState state, int index, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(state);
        var steps = group.Steps;
        if (index < 0 || index >= steps.Count)
        {
            return _texts["Groups.Member.Off"];
        }

        var runtime = group.Runtime;
        var step = steps[index];
        var on = step.Relays.Where(r => state.IsOn(r.Channel)).ToList();
        if (runtime.NextStepAtUtc is { } next && next > nowUtc && GroupPlanner.RunningOn(steps, state, runtime).Any(step.Relays.Contains))
        {
            return _texts["Groups.Member.KeepsRunning", Remaining(next - nowUtc)];
        }

        if (on.Count == step.Relays.Count && on.Count > 0)
        {
            return _texts["Groups.Member.On"];
        }

        if (on.Count > 0)
        {
            return _texts["Groups.Step.Only", string.Join(_texts["Groups.List.Separator"], on.Select(r => r.Name))];
        }

        if (runtime.Phase == GroupPhases.Starting && GroupPlanner.Level(steps, state) == index && runtime.NextStepAtUtc is { } start && start > nowUtc)
        {
            return _texts["Groups.Member.StartsIn", Remaining(start - nowUtc)];
        }

        if (index > 0 && !GroupPlanner.IsFull(steps[index - 1], state))
        {
            return _texts["Groups.Member.Needs", steps[index - 1].Name];
        }

        return index == 0 ? _texts["Groups.Step.StartsFirst"] : _texts["Groups.Member.Off"];
    }

    /// <summary>"Can't run: relay 5 isn't on the board", by the relay's channel.</summary>
    public string MissingRelay(int channel)
    {
        return _texts["Groups.MissingRelay", channel];
    }

    /// <summary>"RELAY 1 + 2 → 3 + 4": the group's relays by their channels, a step's joined by "+".</summary>
    public string Chain(GroupView group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _texts["Groups.Chain", group.Channels];
    }

    /// <summary>
    /// A countdown, shown every second, in the app's one format for a length: "4 min 59 s", "5 min", or "42 s" under a
    /// minute. Rounded up to a whole second, so it never reads 0.
    /// </summary>
    public string Remaining(TimeSpan left)
    {
        return _lengths.Seconds(Math.Max((int)Math.Ceiling(left.TotalSeconds), 1));
    }
}

/// <summary>What a group will do, in plain words, for "New group": its plan on "Review and create" and each relay's note.</summary>
public sealed class GroupPlanText
{
    private readonly IStringLocalizer<Texts> _texts;
    private readonly EventText _lengths;

    public GroupPlanText(IStringLocalizer<Texts> texts, EventText lengths)
    {
        _texts = texts;
        _lengths = lengths;
    }

    /// <summary>"Fan → wait 3 s → Heat".</summary>
    public string Start(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var text = members.Count > 0 ? members[0].RoleName ?? "" : "";
        foreach (var member in members.Skip(1))
        {
            text = _texts["Groups.Plan.Then", text, _lengths.Seconds(member.StartDelaySec), member.RoleName ?? ""];
        }

        return text;
    }

    /// <summary>"Heat off → Run-on for 5 min: Fan, then off": from the bottom, each with its run-on.</summary>
    public string Stop(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(_texts["Groups.Plan.Arrow"], members.Reverse().Select((m, i) => i > 0 && m.RunOnSec > 0
            ? _texts["Groups.Plan.RunsOn", m.RoleName ?? "", _lengths.Seconds(m.RunOnSec)].Value
            : _texts["Groups.Plan.Off", m.RoleName ?? ""].Value));
    }

    /// <summary>"Heat can only be on while Fan is on."</summary>
    public string Interlock(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(_texts["Groups.Plan.Separator"], members.Skip(1).Select((m, i) => _texts["Groups.Plan.Interlock", m.RoleName ?? "", members[i].RoleName ?? ""].Value));
    }

    /// <summary>
    /// "Free", "Inactive · free", "Already in Painting", "Has its own schedules", "Controlled by Door" or "Unavailable to
    /// Contactor".
    /// </summary>
    public string Note(GroupCandidate relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        return relay.GroupName is not null ? _texts["Groups.Note.InGroup", relay.GroupName]
            : relay.Unavailable ? _texts["Groups.Note.Unavailable"]
            : relay.HasSchedules ? _texts["Groups.Note.HasSchedules"]
            : relay.InputName is not null ? _texts["Groups.Note.ControlledBy", relay.InputName]
            : relay.IsActive ? _texts["Groups.Note.Free"]
            : _texts["Groups.Note.InactiveFree"];
    }
}
