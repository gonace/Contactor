namespace Contactor.Boards.Denkovi;

/// <summary>
/// The board answered something that isn't the answer to the request: another transaction, protocol, unit or function,
/// a wrong length or a wrong echo. The connection can't be trusted to be in step after it, so the client closes it.
/// </summary>
public sealed class SmartDenProtocolException : Exception
{
    public SmartDenProtocolException()
        : base("The board's answer wasn't a Modbus answer to the request.")
    {
    }

    public SmartDenProtocolException(string message)
        : base(message)
    {
    }

    public SmartDenProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="message">What is wrong.</param>
    /// <param name="received">The bytes read so far, for the trace.</param>
    public SmartDenProtocolException(string message, byte[] received)
        : base(message)
    {
        Received = received;
    }

    /// <summary>The bytes read before it was clear the answer was wrong; empty when unknown.</summary>
    public IReadOnlyList<byte> Received { get; } = [];
}

/// <summary>The board answered with a Modbus exception (function | 0x80): it refused the request.</summary>
public sealed class SmartDenRefusedException : Exception
{
    public SmartDenRefusedException()
        : base("The board refused the request.")
    {
    }

    public SmartDenRefusedException(string message)
        : base(message)
    {
    }

    public SmartDenRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="code">The Modbus exception code.</param>
    /// <param name="frame">The whole exception answer.</param>
    public SmartDenRefusedException(byte code, byte[] frame)
        : base($"The board refused it: {SmartDenTraceText.ExceptionName(code)}.")
    {
        Code = code;
        Frame = frame;
    }

    /// <summary>The Modbus exception code, e.g. 2 for an address the board doesn't have.</summary>
    public byte Code { get; }

    /// <summary>The exception answer as received.</summary>
    public IReadOnlyList<byte> Frame { get; } = [];
}
