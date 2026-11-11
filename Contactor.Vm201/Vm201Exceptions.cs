namespace Contactor.Vm201;

/// <summary>
/// A command could not be sent because the board is not connected. Commands are never queued for later.
/// </summary>
public sealed class Vm201NotConnectedException : Exception
{
    public Vm201NotConnectedException()
        : base("The VM201 board is not connected.")
    {
    }

    public Vm201NotConnectedException(string message)
        : base(message)
    {
    }

    public Vm201NotConnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The board rejected the user name or password.
/// </summary>
public sealed class Vm201AccessDeniedException : Exception
{
    public Vm201AccessDeniedException()
        : base("The VM201 board rejected the user name or password.")
    {
    }

    public Vm201AccessDeniedException(string message)
        : base(message)
    {
    }

    public Vm201AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
