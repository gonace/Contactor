namespace Contactor.Web.Persistence;

/// <summary>
/// Something that happened, shown on the Events screen.
/// </summary>
public sealed class EventLogEntry
{
    public const int ActionMaxLength = 32;

    public int Id { get; set; }

    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// The board it concerns: every event about a relay, group, input or the board itself has it, and goes with the
    /// board when the board is removed. Null for an event about no board.
    /// </summary>
    public int? BoardId { get; set; }

    /// <summary>Null for events that don't concern one relay.</summary>
    public int? RelayId { get; set; }

    /// <summary>The group it concerns, if any. Not a foreign key: the event outlives a deleted group.</summary>
    public int? GroupId { get; set; }

    /// <summary>The input it concerns, for an input's changes. Not a foreign key, like <see cref="RelayId"/>.</summary>
    public int? InputId { get; set; }

    /// <summary>A user's e-mail, or one of <see cref="EventActors"/>.</summary>
    public string Actor { get; set; } = "";

    /// <summary>One of <see cref="EventActions"/>.</summary>
    public string Action { get; set; } = "";

    /// <summary>JSON, e.g. <c>{"from":"Relay 4","to":"Ventilation – Paint shop"}</c>.</summary>
    public string? Details { get; set; }
}
