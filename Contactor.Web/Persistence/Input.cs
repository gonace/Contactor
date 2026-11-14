namespace Contactor.Web.Persistence;

/// <summary>
/// One of the board's digital inputs, e.g. a door contact or a thermostat. Seeded at start, one per input on the board,
/// and never deleted. Its state is not stored: the board is the source of truth. "Closed" is the board reading it as on.
/// </summary>
public sealed class Input
{
    public const int NameMaxLength = 40;

    /// <summary>
    /// The database's key for the input, in links and events; given by the database, and not its channel. Whatever reads
    /// the board's inputs goes by <see cref="Channel"/>.
    /// </summary>
    public int Id { get; set; }

    /// <summary>The board it is on.</summary>
    public int BoardId { get; set; }

    /// <summary>Its input on that board, 1 to the board's input count; unique per board. The number the screens show.</summary>
    public int Channel { get; set; }

    public string Name { get; set; } = "";

    /// <summary>False hides it from the list by default, e.g. for an input wired to nothing.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>For later: notify people when it opens.</summary>
    public bool NotifyOnOpen { get; set; }

    /// <summary>For later: notify people when it closes.</summary>
    public bool NotifyOnClose { get; set; }

    /// <summary>The name an input is seeded with, until someone gives it one: "Input 2", after its channel.</summary>
    public static string DefaultName(int channel)
    {
        return $"Input {channel}";
    }

    /// <summary>
    /// An input is an electrical contact, so it is "Closed" (the board reads it as on) or "Open": the same words for
    /// every input, on every screen and in Events.
    /// </summary>
    public static string StateText(bool on)
    {
        return on ? "Closed" : "Open";
    }
}
