namespace Contactor.Web.Persistence;

/// <summary>
/// Two or more relays that run as one, in a start order: they start from the top, stop from the bottom, and a relay
/// may only be on while the one above it is (the interlock). E.g. a fan heater: the fan first, then the heat; heat off,
/// the fan runs on, then the fan off.
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

    /// <summary>In start order (<see cref="RelayGroupMember.Position"/>).</summary>
    public List<RelayGroupMember> Members { get; } = [];
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
