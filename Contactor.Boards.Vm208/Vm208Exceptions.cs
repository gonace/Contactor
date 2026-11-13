namespace Contactor.Boards.Vm208;

/// <summary>The board answered something a VM208 doesn't: another status code, too big, or not the JSON it sends.</summary>
public sealed class Vm208ProtocolException : Exception
{
    /// <summary>Whether the board said it was busy: <c>{"Channels":[]}</c>, a status without relays.</summary>
    public bool IsBusy { get; init; }

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
}
