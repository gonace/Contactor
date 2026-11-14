namespace Contactor.Web.Persistence;

/// <summary>
/// One of a board's relays. Seeded at start and never deleted. Its on/off state is not stored: the board is the
/// source of truth.
/// </summary>
public sealed class Relay
{
    public const int NameMaxLength = 40;
    public const int DefaultMaxOnMinutes = 120;

    /// <summary>The name a relay is seeded with, until someone gives it one: "Relay 3", after its channel.</summary>
    public static string DefaultName(int channel)
    {
        return $"Relay {channel}";
    }

    /// <summary>
    /// The database's key for the relay, in links (<c>relay/12</c>) and events; given by the database, and not its
    /// channel. Whatever talks to the board, or reads the board's state, goes by <see cref="Channel"/>.
    /// </summary>
    public int Id { get; set; }

    /// <summary>The board it is on.</summary>
    public int BoardId { get; set; }

    /// <summary>
    /// Its channel on that board, 1 to the board's relay count; unique per board. The number the screens show
    /// ("RELAY 3").
    /// </summary>
    public int Channel { get; set; }

    public string Name { get; set; } = "";

    /// <summary>False hides it from the list by default.</summary>
    public bool IsActive { get; set; }

    /// <summary>Turned off automatically this long after a manual on; null = never.</summary>
    public int? MaxOnMinutes { get; set; }

    public int SortOrder { get; set; }

    public List<Schedule> Schedules { get; } = [];
}
