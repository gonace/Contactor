namespace Contactor.Web.Persistence;

/// <summary>
/// One of the board's digital inputs, e.g. a door contact or a thermostat. Seeded at start, one per input on the board,
/// and never deleted. Its state is not stored: the board is the source of truth. "Closed" is the board reading it as on.
/// </summary>
public sealed class Input
{
    public const int TextMaxLength = 40;

    /// <summary>The board's input, 1 to the board's input count.</summary>
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>What the closed state is called, e.g. "Closed" or "Warm".</summary>
    public string ClosedText { get; set; } = "";

    /// <summary>What the open state is called, e.g. "Open" or "Cold".</summary>
    public string OpenText { get; set; } = "";

    /// <summary>False hides it from the list by default, e.g. for an input wired to nothing.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>For later: notify people when it opens.</summary>
    public bool NotifyOnOpen { get; set; }

    /// <summary>For later: notify people when it closes.</summary>
    public bool NotifyOnClose { get; set; }

    /// <summary>The name an input is seeded with, until someone gives it one: "Input 2".</summary>
    public static string DefaultName(int id)
    {
        return $"Input {id}";
    }

    /// <summary>The text for a state: <see cref="ClosedText"/> when the board reads the input as on.</summary>
    public string StateText(bool on)
    {
        return on ? ClosedText : OpenText;
    }
}
