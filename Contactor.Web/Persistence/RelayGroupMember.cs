namespace Contactor.Web.Persistence;

/// <summary>A relay in a group, at its place in the start order. A relay is in at most one group.</summary>
public sealed class RelayGroupMember
{
    public const int RoleNameMaxLength = 40;
    public const int DefaultStartDelaySec = 3;
    public const int MinStartDelaySec = 1;
    public const int MaxStartDelaySec = 60;
    public const int DefaultRunOnSec = 0;
    public const int MaxRunOnSec = 1800;

    public int GroupId { get; set; }

    public int RelayId { get; set; }

    /// <summary>1 starts first and stops last.</summary>
    public int Position { get; set; }

    /// <summary>Its name in the group, e.g. "Fan" or "Heat".</summary>
    public string RoleName { get; set; } = "";

    /// <summary>How long to wait, once the relay above is confirmed on, before this one starts. Unused for position 1.</summary>
    public int StartDelaySec { get; set; } = DefaultStartDelaySec;

    /// <summary>How long this relay keeps running after the one below it has gone off. Unused for the last.</summary>
    public int RunOnSec { get; set; } = DefaultRunOnSec;

    public RelayGroup? Group { get; set; }
}
