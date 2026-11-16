namespace Contactor.Boards.Denkovi;

/// <summary>
/// The Denkovi DAEnetIP2 v2 as it speaks to the app: SNMPv1 (RFC 1157) over UDP, one OID per request, GetRequest to read
/// and SetRequest to switch. Source: Denkovi's "DAEnetIP2 v2 User Manual" of 21 April 2020, section 9 (tables 9.4 and
/// 9.5), section 6.6.1 (the agent's port and communities) and table 4.1 (the defaults). Its HTTP API (section 8) writes
/// a whole port at once, so the app doesn't use it. See README.md for what is still to check on a real board.
/// </summary>
public static class DaenetIp2Api
{
    /// <summary>The SNMP agent's UDP port as the board ships ("SNMP listen UDP port", table 4.1).</summary>
    public const int DefaultPort = 161;

    /// <summary>
    /// The largest SNMP message the app sends or reads: RFC 1157 asks every agent to take messages up to 484 bytes, and
    /// the app's are far smaller. A datagram that is longer is no answer to the app.
    /// </summary>
    public const int MaxMessageBytes = 484;

    /// <summary>The longest community the board takes ("max 12 symbols", section 6.6.1; table 9.1: 4 to 12).</summary>
    public const int CommunityMaxLength = 12;

    /// <summary>The version field of an SNMPv1 message: 0.</summary>
    public const int Version1 = 0;

    /// <summary>The read-write community as the board ships (table 4.1). Not a secret: it is in the manual.</summary>
    public const string FactoryReadWriteCommunity = "private";

    /// <summary>The read-only community as the board ships (table 4.1). Not a secret: it is in the manual.</summary>
    public const string FactoryReadOnlyCommunity = "000000000000";

    /// <summary>BER: INTEGER.</summary>
    public const byte IntegerTag = 0x02;

    /// <summary>BER: OCTET STRING, the community.</summary>
    public const byte OctetStringTag = 0x04;

    /// <summary>BER: NULL, the value of a GetRequest's variable.</summary>
    public const byte NullTag = 0x05;

    /// <summary>BER: OBJECT IDENTIFIER.</summary>
    public const byte ObjectIdentifierTag = 0x06;

    /// <summary>BER: SEQUENCE.</summary>
    public const byte SequenceTag = 0x30;

    /// <summary>The PDU of snmpget.</summary>
    public const byte GetRequest = 0xA0;

    /// <summary>The PDU of snmpwalk, which the board doesn't take (section 9).</summary>
    public const byte GetNextRequest = 0xA1;

    /// <summary>The PDU of every answer.</summary>
    public const byte GetResponse = 0xA2;

    /// <summary>The PDU of snmpset.</summary>
    public const byte SetRequest = 0xA3;

    /// <summary>error-status: no error.</summary>
    public const int NoError = 0;

    /// <summary>error-status: the answer would be too big.</summary>
    public const int TooBig = 1;

    /// <summary>error-status: an OID the agent doesn't have.</summary>
    public const int NoSuchName = 2;

    /// <summary>error-status: a value the agent doesn't take.</summary>
    public const int BadValue = 3;

    /// <summary>error-status: the variable can't be written.</summary>
    public const int ReadOnly = 4;

    /// <summary>error-status: the agent failed to do it.</summary>
    public const int GenErr = 5;

    /// <summary>Denkovi's prefix, "x" in the manual's tables: .1.3.6.1.4.1.42505.</summary>
    public static IReadOnlyList<int> Enterprise { get; } = [1, 3, 6, 1, 4, 1, 42505];

    /// <summary>The pins of a port: 1 to 8.</summary>
    public const int PinsPerPort = 8;

    /// <summary>The last but one arc of a port's whole byte, <c>pctrlP3byte</c> and <c>pctrlP5byte</c>: x.9.2.n.33.0.</summary>
    public const int PortByteArc = 33;

    /// <summary>A pin's value: Low(0) or High(1).</summary>
    public const int Low = 0;

    /// <summary>A pin's value: Low(0) or High(1).</summary>
    public const int High = 1;

    /// <summary>The largest value of a port's whole byte: INTEGER(0..255).</summary>
    public const int MaxPortValue = 255;

    /// <summary>
    /// One output pin: x.9.2.1.n.0 is <c>pctrlP3pin</c>n (table 9.4), x.9.2.2.n.0 is <c>pctrlP5pin</c>n (table 9.5),
    /// INTEGER { High(1), Low(0) }, read-write.
    /// </summary>
    public static int[] PinOid(DaenetIp2Pin pin)
    {
        return [.. Enterprise, 9, 2, (int)pin.Port, pin.Number, 0];
    }

    /// <summary>A port's 8 pins as one byte, bit 0 = pin 1: x.9.2.1.33.0 (<c>pctrlP3byte</c>) or x.9.2.2.33.0 (<c>pctrlP5byte</c>).</summary>
    public static int[] PortOid(DaenetIp2Port port)
    {
        return [.. Enterprise, 9, 2, (int)port, PortByteArc, 0];
    }
}

/// <summary>The board's two digital output ports, by the arc of their OIDs (x.9.2.1 and x.9.2.2).</summary>
public enum DaenetIp2Port
{
    /// <summary>Port JP3, "P3" (table 9.4).</summary>
    P3 = 1,

    /// <summary>Port JP4, "P5" (table 9.5).</summary>
    P5 = 2,
}

/// <summary>One output pin of the board, e.g. P5.7.</summary>
/// <param name="Port">The port.</param>
/// <param name="Number">The pin, 1 to 8: bit <c>Number − 1</c> of the port's byte.</param>
public readonly record struct DaenetIp2Pin(DaenetIp2Port Port, int Number)
{
    /// <summary>The bit of the pin in its port's byte.</summary>
    public int Bit => 1 << (Number - 1);

    /// <summary>"P5.7", as the manual names it.</summary>
    public override string ToString()
    {
        return $"{Port}.{Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }
}
