using Contactor.Vm201;
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

    /// <summary>"RELÄ 1 → 2".</summary>
    public string Chain => "RELÄ " + string.Join(" → ", Members.Select(m => m.RelayId));

    /// <summary>The relays of the group, as a channel mask.</summary>
    public byte Mask => Members.Aggregate((byte)0, (mask, m) => (byte)(mask | Vm201Channel.Mask(m.RelayId)));

    /// <summary>
    /// Whether the group counts as on: starting, or any relay on and not on its way down. The big button then reads
    /// PÅ, and a press stops the group; during a run-on it reads AV, and a press starts it again.
    /// </summary>
    public bool IsOn(Vm201State state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Runtime.Phase == GroupPhases.Starting
            || (Runtime.Phase != GroupPhases.Stopping && GroupPlanner.AnyOn(Infos, state));
    }
}

/// <summary>The status line under a group's name, in Swedish.</summary>
public static class GroupStatusText
{
    /// <param name="toLocal">From UTC to local time.</param>
    /// <param name="nameOf">"du" for the viewer, otherwise a person's name, "schema" or "auto-av".</param>
    /// <param name="outlook">What the group's schedules say: when a run ends, or when the next one starts.</param>
    public static string For(
        GroupView group,
        Vm201State state,
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
            return "Av";
        }

        if (runtime.Phase == GroupPhases.Starting)
        {
            return $"Startar · steg {Math.Clamp(level + 1, 1, n)} av {n}";
        }

        if (runtime.Phase == GroupPhases.Stopping)
        {
            if (level >= 1 && runtime.NextStepAtUtc is { } next && next > nowUtc)
            {
                return $"Stänger av · {group.Members[level - 1].RoleName} går {Remaining(next - nowUtc)} till";
            }

            return "Stänger av";
        }

        if (!GroupPlanner.AnyOn(group.Infos, state))
        {
            return outlook?.NextOnUtc is { } nextOn
                ? $"Schema: på {RelayStatusText.When(toLocal(nextOn), toLocal(nowUtc))}"
                : "Av";
        }

        // A relay is on while the one above it is off, e.g. switched at the board: the interlock turns it off.
        if (level == 0)
        {
            return "I fel ordning · stängs av";
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
            text += " · bara " + string.Join(", ", group.Members.Take(level).Select(m => m.RoleName));
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

/// <summary>What a group will do, in plain Swedish, for "Granska och skapa".</summary>
public static class GroupPlanText
{
    /// <summary>"Fläkt → vänta 3 s → Värme".</summary>
    public static string Start(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Concat(members.Select((m, i) => i == 0 ? m.RoleName : $" → vänta {EventText.Seconds(m.StartDelaySec)} → {m.RoleName}"));
    }

    /// <summary>"Värme av → Fläkt går 5 min till, sedan av": from the bottom, each with its run-on.</summary>
    public static string Stop(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(" → ", members.Reverse().Select((m, i) => i > 0 && m.RunOnSec > 0
            ? $"{m.RoleName} går {EventText.Seconds(m.RunOnSec)} till, sedan av"
            : $"{m.RoleName} av"));
    }

    /// <summary>"Värme kan bara vara på när Fläkt är på."</summary>
    public static string Interlock(IReadOnlyList<GroupMemberDraft> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return string.Join(" ", members.Skip(1).Select((m, i) => $"{m.RoleName} kan bara vara på när {members[i].RoleName} är på."));
    }
}
