namespace Contactor.Boards.Denkovi;

/// <summary>
/// A datagram that isn't an SNMPv1 message the codec can read, or an answer that isn't the answer to the request: another
/// version, community, PDU or OID, a value of another type or out of its range, or a set that wasn't echoed. Its message
/// never holds the community.
/// </summary>
public sealed class DaenetIp2ProtocolException : Exception
{
    public DaenetIp2ProtocolException()
        : base("The board's answer wasn't an SNMP answer to the request.")
    {
    }

    public DaenetIp2ProtocolException(string message)
        : base(message)
    {
    }

    public DaenetIp2ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The board answered with an error-status: it refused the request.</summary>
public sealed class DaenetIp2RefusedException : Exception
{
    public DaenetIp2RefusedException()
        : base("The board refused the request.")
    {
    }

    public DaenetIp2RefusedException(string message)
        : base(message)
    {
    }

    public DaenetIp2RefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="errorStatus">The answer's error-status, e.g. 2 for an OID the board doesn't have.</param>
    /// <param name="errorIndex">The answer's error-index: 1 for the one variable, or 0.</param>
    public DaenetIp2RefusedException(int errorStatus, int errorIndex)
        : base($"The board refused it: {DaenetIp2TraceText.ErrorName(errorStatus)}.")
    {
        ErrorStatus = errorStatus;
        ErrorIndex = errorIndex;
    }

    /// <summary>The SNMP error-status, e.g. 3 for a value the board doesn't take.</summary>
    public int ErrorStatus { get; }

    /// <summary>The SNMP error-index.</summary>
    public int ErrorIndex { get; }
}
