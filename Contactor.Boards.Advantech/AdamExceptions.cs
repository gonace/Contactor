namespace Contactor.Boards.Advantech;

/// <summary>
/// The module answered something that isn't the answer to the request: another transaction, protocol, unit or function,
/// a wrong length or a wrong echo. The connection can't be trusted to be in step after it, so the client closes it.
/// </summary>
public sealed class AdamProtocolException : Exception
{
    public AdamProtocolException()
        : base("The module's answer wasn't a Modbus answer to the request.")
    {
    }

    public AdamProtocolException(string message)
        : base(message)
    {
    }

    public AdamProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">What is wrong.</param>
    /// <param name="received">The bytes read so far, for the trace.</param>
    public AdamProtocolException(string message, byte[] received)
        : base(message)
    {
        Received = received;
    }

    /// <summary>The bytes read before it was clear the answer was wrong; empty when unknown.</summary>
    public IReadOnlyList<byte> Received { get; } = [];
}

/// <summary>The module answered with a Modbus exception (function | 0x80): it refused the request.</summary>
public sealed class AdamRefusedException : Exception
{
    public AdamRefusedException()
        : base("The module refused the request.")
    {
    }

    public AdamRefusedException(string message)
        : base(message)
    {
    }

    public AdamRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="code">The Modbus exception code.</param>
    /// <param name="frame">The whole exception answer.</param>
    public AdamRefusedException(byte code, byte[] frame)
        : base($"The module refused it: {AdamTraceText.ExceptionName(code)}.")
    {
        Code = code;
        Frame = frame;
    }

    /// <summary>The Modbus exception code, e.g. 2 for an address the module doesn't have.</summary>
    public byte Code { get; }

    /// <summary>The exception answer as received.</summary>
    public IReadOnlyList<byte> Frame { get; } = [];
}
