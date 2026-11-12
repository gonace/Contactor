namespace Contactor.Web.Persistence;

/// <summary>
/// One of the board's relays. Seeded at start and never deleted. Its on/off state is not stored: the board is the
/// source of truth.
/// </summary>
public sealed class Relay
{
    public const int NameMaxLength = 40;
    public const int DefaultMaxOnMinutes = 120;

    /// <summary>The name a relay is seeded with, until someone gives it one: "Relay 3".</summary>
    public static string DefaultName(int id)
    {
        return $"Relay {id}";
    }

    /// <summary>The board's channel, 1 to the board's relay count.</summary>
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>False hides it from the list by default.</summary>
    public bool IsActive { get; set; }

    /// <summary>Turned off automatically this long after a manual on; null = never.</summary>
    public int? MaxOnMinutes { get; set; }

    public int SortOrder { get; set; }

    public List<Schedule> Schedules { get; } = [];
}
