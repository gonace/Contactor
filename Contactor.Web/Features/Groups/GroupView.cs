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
public sealed record GroupMemberView(int RelayId, int Channel, int Position, string RoleName, int StartDelaySec, int RunOnSec);

/// <summary>A group as the screens show it: its setup and where its sequence is. Whether relays are on comes from the board.</summary>
public sealed record GroupView(
    int Id,
    string Name,
    string Icon,
    int? MaxOnMinutes,
    IReadOnlyList<GroupMemberView> Members,
    GroupRuntime Runtime)
{
    /// <summary>The group's board: all its relays are on it.</summary>
    public int BoardId { get; init; }

    /// <summary>The members as the planner sees them.</summary>
    public IReadOnlyList<GroupMemberInfo> Infos =>
        [.. Members.Select(m => new GroupMemberInfo(m.Position, m.RelayId, m.Channel, m.RoleName, m.StartDelaySec, m.RunOnSec))];

    /// <summary>
    /// The channel of the first relay of the group the board doesn't have (left from a board with more relays), or
    /// null. Such a group can't run: its relays are kept off until it is changed or deleted.
    /// </summary>
    public int? MissingRelay(int relayCount)
    {
        return Members.Where(m => m.Channel > relayCount).Select(m => (int?)m.Channel).FirstOrDefault();
    }

    /// <summary>"1 → 2": the relays' channels, in start order.</summary>
    public string Channels => string.Join(" → ", Members.Select(m => m.Channel));

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
            || (Runtime.Phase != GroupPhases.Stopping && GroupPlanner.AnyOn(Infos, state));
    }
}

/// <summary>A group's status in words, in the app's language: the line under its name and each relay's note.</summary>
public sealed class GroupStatusText
{
    private readonly IStringLocalizer<GroupsTexts> _texts;
    private readonly RelayStatusText _relayStatus;

    public GroupStatusText(IStringLocalizer<GroupsTexts> texts, RelayStatusText relayStatus)
    {
        _texts = texts;
        _relayStatus = relayStatus;
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
        var level = GroupPlanner.Level(group.Infos, state);
        var n = group.Members.Count;
        if (n == 0)
        {
            return _texts["Groups.Status.Off"];
        }

        if (runtime.Phase == GroupPhases.Starting)
        {
            // Waiting out the start delay before the next relay: count it down.
            if (level < n && runtime.NextStepAtUtc is { } nextStart && nextStart > nowUtc)
            {
                return _texts["Groups.Status.StartingIn", group.Members[level].RoleName, Remaining(nextStart - nowUtc)];
            }

            return _texts["Groups.Status.StartingStep", Math.Clamp(level + 1, 1, n), n];
        }

        if (runtime.Phase == GroupPhases.Stopping)
        {
            if (level >= 1 && runtime.NextStepAtUtc is { } next && next > nowUtc)
            {
                return _texts["Groups.Status.StoppingRunOn", group.Members[level - 1].RoleName, Remaining(next - nowUtc)];
            }

            return _texts["Groups.Status.Stopping"];
        }

        if (!GroupPlanner.AnyOn(group.Infos, state))
        {
            return outlook?.NextOnUtc is { } nextOn
                ? _texts["Groups.Status.ScheduleOn", _relayStatus.When(toLocal(nextOn), toLocal(nowUtc))]
                : _texts["Groups.Status.Off"];
        }

        // A relay is on while the one above it is off, e.g. switched at the board: the interlock turns it off.
        if (level == 0)
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

        // Only some of it runs, e.g. the fan alone.
        return level < n
            ? _texts["Groups.Status.Only", text, string.Join(_texts["Groups.List.Separator"], group.Members.Take(level).Select(m => m.RoleName))]
            : text;
    }

    /// <summary>
    /// The state of the relay at <paramref name="index"/> (from 0, in start order) on the Group screen: "Keeps
    /// running for 4 min 59 s", "On", "Starts in 3 s", "Needs Fan to be on" or "Off".
    /// </summary>
    public string MemberNote(GroupView group, BoardState state, int index, bool on, string? blockedBy, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(state);
        var runtime = group.Runtime;
        var level = GroupPlanner.Level(group.Infos, state);
        if (on && runtime.Phase == GroupPhases.Stopping && index == level - 1 && runtime.NextStepAtUtc is { } next && next > nowUtc)
        {
            return _texts["Groups.Member.KeepsRunning", Remaining(next - nowUtc)];
        }

        if (on)
        {
            return _texts["Groups.Member.On"];
        }

        if (runtime.Phase == GroupPhases.Starting && index == level && runtime.NextStepAtUtc is { } start && start > nowUtc)
        {
            return _texts["Groups.Member.StartsIn", Remaining(start - nowUtc)];
        }

        return blockedBy is not null ? _texts["Groups.Member.Needs", blockedBy] : _texts["Groups.Member.Off"];
    }

    /// <summary>"Can't run: relay 5 isn't on the board", by the relay's channel.</summary>
    public string MissingRelay(int channel)
    {
        return _texts["Groups.MissingRelay", channel];
    }

    /// <summary>"RELAY 1 → 2": the group's relays by their channels.</summary>
    public string Chain(GroupView group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return _texts["Groups.Chain", group.Channels];
    }

    /// <summary>
    /// A countdown, shown every second, in the app's one format for a length: "4 min 59 s", "5 min", or "42 s" under a
    /// minute. Rounded up to a whole second, so it never reads 0.
    /// </summary>
    public static string Remaining(TimeSpan left)
    {
        return EventText.Seconds(Math.Max((int)Math.Ceiling(left.TotalSeconds), 1));
    }
}

/// <summary>What a group will do, in plain words, for "New group": its plan on "Review and create" and each relay's note.</summary>
public sealed class GroupPlanText
{
    private readonly IStringLocalizer<GroupsTexts> _texts;

    public GroupPlanText(IStringLocalizer<GroupsTexts> texts)
    {
        _texts = texts;
    }

    /// <summary>"Fan → wait 3 s → Heat".</summary>
    public string Start(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        var text = members.Count > 0 ? members[0].RoleName ?? "" : "";
        foreach (var member in members.Skip(1))
        {
            text = _texts["Groups.Plan.Then", text, EventText.Seconds(member.StartDelaySec), member.RoleName ?? ""];
        }

        return text;
    }

    /// <summary>"Heat off → Fan keeps running for 5 min, then off": from the bottom, each with its run-on.</summary>
    public string Stop(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(_texts["Groups.Plan.Arrow"], members.Reverse().Select((m, i) => i > 0 && m.RunOnSec > 0
            ? _texts["Groups.Plan.RunsOn", m.RoleName ?? "", EventText.Seconds(m.RunOnSec)].Value
            : _texts["Groups.Plan.Off", m.RoleName ?? ""].Value));
    }

    /// <summary>"Heat can only be on while Fan is on."</summary>
    public string Interlock(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(_texts["Groups.Plan.Separator"], members.Skip(1).Select((m, i) => _texts["Groups.Plan.Interlock", m.RoleName ?? "", members[i].RoleName ?? ""].Value));
    }

    /// <summary>"Free", "Inactive · free", "Already in Painting", "Has its own schedules" or "Controlled by Door".</summary>
    public string Note(GroupCandidate relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        return relay.GroupName is not null ? _texts["Groups.Note.InGroup", relay.GroupName]
            : relay.HasSchedules ? _texts["Groups.Note.HasSchedules"]
            : relay.InputName is not null ? _texts["Groups.Note.ControlledBy", relay.InputName]
            : relay.IsActive ? _texts["Groups.Note.Free"]
            : _texts["Groups.Note.InactiveFree"];
    }
}
