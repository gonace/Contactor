namespace Contactor.Web.Persistence;

/// <summary>
/// Something that happened, shown on the Händelser screen.
/// </summary>
public sealed class EventLogEntry
{
    public const int ActionMaxLength = 32;

    public int Id { get; set; }

    public DateTime TimestampUtc { get; set; }

    /// <summary>Null for events that don't concern one relay.</summary>
    public int? RelayId { get; set; }

    /// <summary>The group it concerns, if any. Not a foreign key: the event outlives a deleted group.</summary>
    public int? GroupId { get; set; }

    /// <summary>A user's e-mail, or one of <see cref="EventActors"/>.</summary>
    public string Actor { get; set; } = "";

    /// <summary>One of <see cref="EventActions"/>.</summary>
    public string Action { get; set; } = "";

    /// <summary>JSON, e.g. <c>{"from":"Relä 4","to":"Ventilation – Lackrummet"}</c>.</summary>
    public string? Details { get; set; }
}
