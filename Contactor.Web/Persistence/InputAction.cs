namespace Contactor.Web.Persistence;

/// <summary>
/// What an input does to one relay or one group when it closes and when it opens: exactly one of
/// <see cref="RelayId"/> and <see cref="GroupId"/> is set. True turns it on, false turns it off, null does nothing.
/// </summary>
public sealed class InputAction
{
    public int Id { get; set; }

    public int InputId { get; set; }

    /// <summary>The relay it switches, or null for a group. A relay in a group is only switched through its group.</summary>
    public int? RelayId { get; set; }

    public Relay? Relay { get; set; }

    /// <summary>The group it starts and stops, or null for a relay.</summary>
    public int? GroupId { get; set; }

    public RelayGroup? Group { get; set; }

    /// <summary>When the input closes: true turns on, false turns off, null does nothing.</summary>
    public bool? WhenClosed { get; set; }

    /// <summary>When the input opens: true turns on, false turns off, null does nothing.</summary>
    public bool? WhenOpened { get; set; }
}
