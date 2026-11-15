namespace Contactor.Web.Persistence;

/// <summary>
/// Two or more relays that run as one, in steps: the steps start from the top, stop from the bottom, and a relay may
/// only be on while every relay of every step above it is (the interlock). The relays of a step switch together. E.g. a
/// fan heater: the fan first, then the heat; heat off, the fan runs on, then the fan off.
/// </summary>
public sealed class RelayGroup
{
    public const int NameMaxLength = 40;
    public const int MinMembers = 2;
    public const int DefaultMaxOnMinutes = 120;

    public int Id { get; set; }

    /// <summary>The board its relays are on: a group's relays are all on one board.</summary>
    public int BoardId { get; set; }

    public string Name { get; set; } = "";

    /// <summary>One of <see cref="GroupIcons"/>.</summary>
    public string Icon { get; set; } = GroupIcons.Fan;

    /// <summary>The whole group stops (with its run-on times) this long after a person started it; null = never.</summary>
    public int? MaxOnMinutes { get; set; }

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Its steps; in start order by <see cref="RelayGroupStep.Position"/>.</summary>
    public List<RelayGroupStep> Steps { get; } = [];

    /// <summary>Its relays, of every step.</summary>
    public List<RelayGroupMember> Members { get; } = [];

    /// <summary>Adds a step below the others with the given relays, in that order.</summary>
    public RelayGroupStep AddStep(
        string name,
        IReadOnlyList<int> relayIds,
        int startDelaySec = RelayGroupStep.DefaultStartDelaySec,
        int runOnSec = RelayGroupStep.DefaultRunOnSec)
    {
        ArgumentNullException.ThrowIfNull(relayIds);
        var step = new RelayGroupStep
        {
            Group = this,
            Position = Steps.Count == 0 ? 1 : Steps.Max(s => s.Position) + 1,
            Name = name,
            StartDelaySec = startDelaySec,
            RunOnSec = runOnSec,
        };
        Steps.Add(step);
        var order = 1;
        foreach (var relayId in relayIds)
        {
            var member = new RelayGroupMember { Group = this, Step = step, RelayId = relayId, Order = order++ };
            step.Members.Add(member);
            Members.Add(member);
        }

        return step;
    }

    /// <summary>Adds a step of one relay below the others.</summary>
    public RelayGroupStep AddStep(
        string name,
        int relayId,
        int startDelaySec = RelayGroupStep.DefaultStartDelaySec,
        int runOnSec = RelayGroupStep.DefaultRunOnSec)
    {
        return AddStep(name, [relayId], startDelaySec, runOnSec);
    }
}

/// <summary>The icons a group can have (Font Awesome Free's names for them).</summary>
public static class GroupIcons
{
    public const string Fan = "fan";
    public const string Flame = "flame";
    public const string Bulb = "bulb";
    public const string Wind = "wind";
    public const string Spray = "spray";
    public const string Drop = "drop";
    public const string Wrench = "wrench";
    public const string Bolt = "bolt";

    public const int MaxLength = 16;

    public static IReadOnlyList<string> All { get; } = [Fan, Flame, Bulb, Wind, Spray, Drop, Wrench, Bolt];
}
