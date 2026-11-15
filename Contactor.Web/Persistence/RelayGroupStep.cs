namespace Contactor.Web.Persistence;

/// <summary>
/// A step of a group's start order: one or more relays that switch together, e.g. "Fans" (Fan 1 + Fan 2). Steps start
/// from the top and stop from the bottom, and a relay may only be on while every relay of every step above it is.
/// </summary>
public sealed class RelayGroupStep
{
    public const int NameMaxLength = 40;
    public const int DefaultStartDelaySec = 3;
    public const int MinStartDelaySec = 1;
    public const int MaxStartDelaySec = 60;
    public const int DefaultRunOnSec = 0;
    public const int MaxRunOnSec = 1800;

    public int Id { get; set; }

    public int GroupId { get; set; }

    /// <summary>1 starts first and stops last.</summary>
    public int Position { get; set; }

    /// <summary>Its name in the group, e.g. "Fans" or "Heat".</summary>
    public string Name { get; set; } = "";

    /// <summary>How long to wait, once the step above is confirmed on, before this one starts. Unused for step 1.</summary>
    public int StartDelaySec { get; set; } = DefaultStartDelaySec;

    /// <summary>How long this step keeps running after the step below it has gone off. Unused for the last.</summary>
    public int RunOnSec { get; set; } = DefaultRunOnSec;

    public RelayGroup? Group { get; set; }

    /// <summary>Its relays, in <see cref="RelayGroupMember.Order"/>.</summary>
    public List<RelayGroupMember> Members { get; } = [];
}
