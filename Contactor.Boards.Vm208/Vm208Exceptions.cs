namespace Contactor.Boards.Vm208;

/// <summary>The board answered something a VM208 doesn't: another status code, too big, or not the JSON it sends.</summary>
public sealed class Vm208ProtocolException : Exception
{
    public Vm208ProtocolException()
        : base("The VM208's answer wasn't what a VM208 sends.")
    {
    }

    public Vm208ProtocolException(string message)
        : base(message)
    {
    }

    public Vm208ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The answer was the board's own, read in full: it was too busy and sent no relays (<c>{"Channels":[]}</c>). To a
    /// command that is a refusal: the board answered, so it doesn't count toward losing it.
    /// </summary>
    public bool IsBusy { get; init; }
}
