namespace Contactor.Web.Persistence;

/// <summary>A relay in a group, in one of its steps. A relay is in at most one group.</summary>
public sealed class RelayGroupMember
{
    public int GroupId { get; set; }

    public int RelayId { get; set; }

    /// <summary>Its step, in the same group (the database holds them to that).</summary>
    public int StepId { get; set; }

    /// <summary>Its place among the relays of its step, from 1.</summary>
    public int Order { get; set; } = 1;

    public RelayGroup? Group { get; set; }

    public RelayGroupStep? Step { get; set; }

    /// <summary>The relay, e.g. for its channel on the board.</summary>
    public Relay? Relay { get; set; }
}
