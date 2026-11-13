namespace Contactor.Boards.Devantech;

/// <summary>
/// The board answered a value its answer can't have, e.g. 05 to a relay command. The connection can't be trusted to
/// be in step after it, so the client closes it.
/// </summary>
public sealed class DevantechProtocolException : Exception
{
    public DevantechProtocolException()
        : base("The board's answer wasn't one a Devantech board sends.")
    {
    }

    public DevantechProtocolException(string message)
        : base(message)
    {
    }

    public DevantechProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">What is wrong.</param>
    /// <param name="received">The answer, for the trace.</param>
    public DevantechProtocolException(string message, byte[] received)
        : base(message)
    {
        Received = received;
    }

    /// <summary>The answer as received; empty when unknown.</summary>
    public IReadOnlyList<byte> Received { get; } = [];
}

/// <summary>The board answered 01 to a relay command: it didn't do it.</summary>
public sealed class DevantechRefusedException : Exception
{
    public DevantechRefusedException()
        : base("The board refused the command.")
    {
    }

    public DevantechRefusedException(string message)
        : base(message)
    {
    }

    public DevantechRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The board refused the password, or asks for one and the app has none.</summary>
public sealed class DevantechAccessDeniedException : Exception
{
    public DevantechAccessDeniedException()
        : base("The Devantech board refused the password.")
    {
    }

    public DevantechAccessDeniedException(string message)
        : base(message)
    {
    }

    public DevantechAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Another model of board is at the address: the app switches nothing on it.</summary>
public sealed class DevantechWrongModelException : Exception
{
    public DevantechWrongModelException()
        : base("The board at this address is another model.")
    {
    }

    public DevantechWrongModelException(string message)
        : base(message)
    {
    }

    public DevantechWrongModelException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
