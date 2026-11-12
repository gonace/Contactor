namespace Contactor.Boards.Vm204;

/// <summary>The board refused the user name and password, or the key from an earlier login.</summary>
public sealed class Vm204AccessDeniedException : Exception
{
    public Vm204AccessDeniedException()
        : base("The VM204 refused the user name or password.")
    {
    }

    public Vm204AccessDeniedException(string message)
        : base(message)
    {
    }

    public Vm204AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The board answered something a VM204 doesn't: another status code, too big, or not the JSON it sends.</summary>
public sealed class Vm204ProtocolException : Exception
{
    public Vm204ProtocolException()
        : base("The VM204's answer wasn't what a VM204 sends.")
    {
    }

    public Vm204ProtocolException(string message)
        : base(message)
    {
    }

    public Vm204ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
