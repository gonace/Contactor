using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Contactor.Boards.Denkovi;

/// <summary>
/// One SNMP request the app sends: a GetRequest or a SetRequest for one OID of the closed list (<see cref="DaenetIp2Oid"/>),
/// with what it needs to check the answer. Only <see cref="DaenetIp2Snmp"/> makes one, and never from an OID given as
/// data. A class, not a record, so the community it holds can never end up in a log through a generated ToString.
/// </summary>
public sealed class DaenetIp2Request
{
    private readonly byte[] _community;
    private readonly byte[] _oid;
    private readonly int _pduOffset;

    internal DaenetIp2Request(byte[] message, int pduOffset, byte pduType, int requestId, byte[] community, DaenetIp2Oid target, byte[] oidBytes, int? value)
    {
        Message = message;
        _pduOffset = pduOffset;
        PduType = pduType;
        RequestId = requestId;
        _community = community;
        Target = target;
        _oid = oidBytes;
        Value = value;
    }

    /// <summary>The whole datagram as sent. It holds the community: never trace or log it whole, only <see cref="Pdu"/>.</summary>
    public byte[] Message { get; }

    /// <summary>The PDU alone (request id, error fields and the variable), without the version and the community.</summary>
    public ReadOnlySpan<byte> Pdu => Message.AsSpan(_pduOffset);

    /// <summary><see cref="DaenetIp2Api.GetRequest"/> or <see cref="DaenetIp2Api.SetRequest"/>.</summary>
    public byte PduType { get; }

    /// <summary>The request id the answer must carry.</summary>
    public int RequestId { get; }

    /// <summary>The OID it asks for, one of <see cref="DaenetIp2Oid.All"/>.</summary>
    public DaenetIp2Oid Target { get; }

    /// <summary>The OID's arcs, e.g. 1.3.6.1.4.1.42505.9.2.2.33.0.</summary>
    public IReadOnlyList<int> Oid => Target.Arcs;

    /// <summary>The value a SetRequest writes; null for a GetRequest.</summary>
    public int? Value { get; }

    /// <summary>
    /// The largest value the answer may carry (<see cref="DaenetIp2Oid.MaxValue"/>): 1 for a pin, 255 for a port's byte
    /// or the restart pins, 1023 for an analog input, 65535 for the firmware version.
    /// </summary>
    public int MaxValue => Target.MaxValue;

    internal ReadOnlySpan<byte> Community => _community;

    internal ReadOnlySpan<byte> OidBytes => _oid;
}

/// <summary>
/// An SNMPv1 message as read from a datagram: a request (to the simulator) or an answer (to the app), with one variable.
/// A class, not a record, so the community it holds can never end up in a log; <see cref="HasCommunity"/> compares it.
/// </summary>
public sealed class DaenetIp2Message
{
    private readonly byte[] _community;
    private readonly byte[] _oid;
    private readonly byte[] _value;
    private readonly byte[] _pdu;

    internal DaenetIp2Message(int version, byte[] community, byte pduType, int requestId, int errorStatus, int errorIndex, int[] oid, byte[] oidBytes, byte valueType, int? value, int valueLength, byte[] valueTlv, byte[] pdu)
    {
        ValueLength = valueLength;
        Version = version;
        _community = community;
        PduType = pduType;
        RequestId = requestId;
        ErrorStatus = errorStatus;
        ErrorIndex = errorIndex;
        Oid = oid;
        _oid = oidBytes;
        ValueType = valueType;
        Value = value;
        _value = valueTlv;
        _pdu = pdu;
    }

    /// <summary>The version field: 0 for SNMPv1.</summary>
    public int Version { get; }

    /// <summary>The PDU's tag, e.g. <see cref="DaenetIp2Api.GetResponse"/>.</summary>
    public byte PduType { get; }

    public int RequestId { get; }

    public int ErrorStatus { get; }

    public int ErrorIndex { get; }

    /// <summary>The variable's OID.</summary>
    public IReadOnlyList<int> Oid { get; }

    /// <summary>The BER tag of the variable's value, e.g. <see cref="DaenetIp2Api.IntegerTag"/> or <see cref="DaenetIp2Api.NullTag"/>.</summary>
    public byte ValueType { get; }

    /// <summary>The value when it is an INTEGER of at most 4 bytes, in two's complement; null otherwise.</summary>
    public int? Value { get; }

    /// <summary>How many bytes the value's content has, e.g. 1 for <c>02 01 FF</c>.</summary>
    public int ValueLength { get; }

    /// <summary>The PDU alone, without the version and the community: what a trace may show.</summary>
    public ReadOnlySpan<byte> Pdu => _pdu;

    internal ReadOnlySpan<byte> OidBytes => _oid;

    internal ReadOnlySpan<byte> ValueTlv => _value;

    /// <summary>Whether the message carries this community, byte for byte.</summary>
    public bool HasCommunity(ReadOnlySpan<byte> community)
    {
        return community.SequenceEqual(_community);
    }

    /// <summary>Whether the message carries this community.</summary>
    public bool HasCommunity(string community)
    {
        ArgumentNullException.ThrowIfNull(community);
        return HasCommunity(Encoding.ASCII.GetBytes(community));
    }
}

/// <summary>
/// Our own small SNMPv1 codec (RFC 1157, BER), only for what the app and the simulator use: a GetRequest, a SetRequest
/// and a GetResponse, each with one variable, as the board takes one OID per request (manual, section 9). A request the
/// app sends (<see cref="DaenetIp2Request"/>) is only ever for an OID of the closed list (<see cref="DaenetIp2Oid"/>);
/// <see cref="Encode"/> writes any other message, for the simulator's tests, as bytes the client never sends. A message is
/// a SEQUENCE of the version (0), the community and the PDU; the PDU holds the request id, the error-status, the
/// error-index and the variable (its OID and value). Lengths in the short form and the long form up to 2 bytes; a
/// message is at most <see cref="DaenetIp2Api.MaxMessageBytes"/> bytes, and every length is checked against what is
/// left, so whatever a datagram holds can't read past it.
/// </summary>
public static class DaenetIp2Snmp
{
    // Arcs in an OID, and bytes in one arc: more is no OID the board has.
    private const int MaxOidArcs = 32;
    private const int MaxArcBytes = 5;

    /// <summary>snmpget of a port's whole byte, <c>pctrlP3byte</c> or <c>pctrlP5byte</c>: the answer is 0 to 255.</summary>
    public static DaenetIp2Request GetPort(int requestId, string community, DaenetIp2Port port)
    {
        return Get(requestId, community, DaenetIp2Oid.Port(port));
    }

    /// <summary>
    /// snmpget of one analog input of P6, 1 to 8, <c>pctrlP6pin</c>n: the answer is 0 to 1023 (table 9.6). One OID per
    /// request, as for every get (section 9).
    /// </summary>
    public static DaenetIp2Request GetAnalog(int requestId, string community, int input)
    {
        return Get(requestId, community, DaenetIp2Oid.Analog(input));
    }

    /// <summary>snmpset of one pin, High(1) or Low(0); the board echoes the value.</summary>
    public static DaenetIp2Request SetPin(int requestId, string community, DaenetIp2Pin pin, bool high)
    {
        return Set(requestId, community, DaenetIp2Oid.Pin(pin), high ? DaenetIp2Api.High : DaenetIp2Api.Low);
    }

    /// <summary>snmpget of the firmware version, <c>cfgVersion</c>: the answer is 0 to 65535 (table 9.1).</summary>
    public static DaenetIp2Request GetFirmwareVersion(int requestId, string community)
    {
        return Get(requestId, community, DaenetIp2Oid.FirmwareVersion);
    }

    /// <summary>snmpget of the ICMP monitoring's restart pins, <c>cfgP5DupRST</c>: the answer is 0 to 255 (table 9.1).</summary>
    public static DaenetIp2Request GetRestartPins(int requestId, string community)
    {
        return Get(requestId, community, DaenetIp2Oid.RestartPins);
    }

    /// <summary>snmpset of the restart pins, <c>cfgP5DupRST</c>, 0 to 255, bit 0 = P5.1; the board echoes the value.</summary>
    public static DaenetIp2Request SetRestartPins(int requestId, string community, int pins)
    {
        return Set(requestId, community, DaenetIp2Oid.RestartPins, pins);
    }

    /// <summary>A GetRequest of an OID of the closed list; the answer may carry 0 to its <see cref="DaenetIp2Oid.MaxValue"/>.</summary>
    public static DaenetIp2Request Get(int requestId, string community, DaenetIp2Oid oid)
    {
        ArgumentNullException.ThrowIfNull(oid);
        return Request(DaenetIp2Api.GetRequest, requestId, community, oid, null);
    }

    /// <summary>
    /// A SetRequest of an INTEGER for a read-write OID of the closed list, 0 to its <see cref="DaenetIp2Oid.MaxValue"/>;
    /// the answer must echo it. Throws <see cref="ArgumentException"/> for a read-only OID and
    /// <see cref="ArgumentOutOfRangeException"/> for a value out of range, before anything is made.
    /// </summary>
    public static DaenetIp2Request Set(int requestId, string community, DaenetIp2Oid oid, int value)
    {
        ArgumentNullException.ThrowIfNull(oid);
        if (!oid.Writable)
        {
            throw new ArgumentException($"{oid.Name} is read-only: the app never sets it.", nameof(oid));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, oid.MaxValue);
        return Request(DaenetIp2Api.SetRequest, requestId, community, oid, value);
    }

    /// <summary>
    /// Any message with one variable, as bytes: what another SNMP manager could send, e.g. a GetNextRequest or an OID the
    /// board doesn't have. Only for the simulator's tests: the client sends <see cref="DaenetIp2Request"/> alone, and never
    /// this. <paramref name="value"/> is an INTEGER, or NULL when null.
    /// </summary>
    public static byte[] Encode(byte pduType, int requestId, string community, IReadOnlyList<int> oid, int? value)
    {
        var valueTlv = value is { } v ? Integer(v) : [DaenetIp2Api.NullTag, 0];
        return Message(pduType, requestId, CommunityBytes(community), DaenetIp2Api.NoError, 0, Oid(oid), valueTlv, out _);
    }

    /// <summary>
    /// A GetResponse, as the board sends it: the request's id and community, the error-status and error-index, and the
    /// variable with an INTEGER, or NULL when <paramref name="value"/> is null.
    /// </summary>
    public static byte[] Response(int requestId, string community, IReadOnlyList<int> oid, int? value, int errorStatus = DaenetIp2Api.NoError, int errorIndex = 0)
    {
        var valueTlv = value is { } v ? Integer(v) : [DaenetIp2Api.NullTag, 0];
        return Message(DaenetIp2Api.GetResponse, requestId, CommunityBytes(community), errorStatus, errorIndex, Oid(oid), valueTlv, out _);
    }

    /// <summary>A GetResponse that echoes a request's variable as it came, for an answer with an error-status.</summary>
    internal static byte[] Response(DaenetIp2Message request, string community, int errorStatus, int errorIndex, int? value = null)
    {
        var valueTlv = value is { } v ? Integer(v) : request.ValueTlv.ToArray();
        return Message(DaenetIp2Api.GetResponse, request.RequestId, CommunityBytes(community), errorStatus, errorIndex, request.OidBytes.ToArray(), valueTlv, out _);
    }

    /// <summary>
    /// Reads a datagram as an SNMPv1-shaped message with one variable: a GetRequest, GetNextRequest, GetResponse or
    /// SetRequest. Throws <see cref="DaenetIp2ProtocolException"/> for anything else: too long, cut short, trailing
    /// bytes, a length or a type the codec doesn't take, or another number of variables. The version is read, not
    /// checked: the caller decides.
    /// </summary>
    public static DaenetIp2Message Decode(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length > DaenetIp2Api.MaxMessageBytes)
        {
            throw Unreadable($"The datagram is {Number(datagram.Length)} bytes, more than an SNMP message of {Number(DaenetIp2Api.MaxMessageBytes)}.");
        }

        var outer = new Reader(datagram);
        var message = new Reader(outer.Read(DaenetIp2Api.SequenceTag, "The message"));
        outer.End("The datagram");

        var version = ReadInt32(message.Read(DaenetIp2Api.IntegerTag, "The version"), "The version");
        var community = message.Read(DaenetIp2Api.OctetStringTag, "The community").ToArray();
        var pduStart = datagram.Length - message.Remaining;
        var pduContent = message.ReadAny("The PDU", out var pduType);
        message.End("The message");
        if (pduType is not (DaenetIp2Api.GetRequest or DaenetIp2Api.GetNextRequest or DaenetIp2Api.GetResponse or DaenetIp2Api.SetRequest))
        {
            throw Unreadable($"The PDU has type {pduType:X2}, not a request or an answer.");
        }

        var pdu = new Reader(pduContent);
        var requestId = ReadInt32(pdu.Read(DaenetIp2Api.IntegerTag, "The request id"), "The request id");
        var errorStatus = ReadInt32(pdu.Read(DaenetIp2Api.IntegerTag, "The error-status"), "The error-status");
        var errorIndex = ReadInt32(pdu.Read(DaenetIp2Api.IntegerTag, "The error-index"), "The error-index");
        var list = new Reader(pdu.Read(DaenetIp2Api.SequenceTag, "The variables"));
        pdu.End("The PDU");
        if (list.IsEmpty)
        {
            throw Unreadable("The message has no variable.");
        }

        var binding = new Reader(list.Read(DaenetIp2Api.SequenceTag, "The variable"));
        if (!list.IsEmpty)
        {
            throw Unreadable("The message has more than one variable.");
        }

        var oidBytes = binding.Read(DaenetIp2Api.ObjectIdentifierTag, "The OID").ToArray();
        var oid = DecodeOid(oidBytes);
        var valueStart = binding.Remaining;
        var valueContent = binding.ReadAny("The value", out var valueType);
        binding.End("The variable");
        var valueTlv = new byte[valueStart];
        int? value = null;
        if (valueType == DaenetIp2Api.IntegerTag)
        {
            value = ReadInt32(valueContent, "The value");
        }
        else if (valueType == DaenetIp2Api.NullTag && valueContent.Length != 0)
        {
            throw Unreadable("The NULL value isn't empty.");
        }

        // The value's whole TLV, for an error answer that echoes it: the last bytes of the variable.
        var pduBytes = datagram[pduStart..].ToArray();
        pduBytes.AsSpan(pduBytes.Length - valueStart).CopyTo(valueTlv);
        return new DaenetIp2Message(version, community, pduType, requestId, errorStatus, errorIndex, oid, oidBytes, valueType, value, valueContent.Length, valueTlv, pduBytes);
    }

    /// <summary>
    /// The value of <paramref name="answer"/>, once it is checked to be the answer to <paramref name="request"/>: SNMPv1,
    /// a GetResponse with the same request id and community, error-index 0 or 1, the same OID, an INTEGER from 0 to
    /// the request's <see cref="DaenetIp2Request.MaxValue"/> (an INTEGER of one byte read unsigned, 0 to 255), and for a
    /// set the value that was set. Throws
    /// <see cref="DaenetIp2RefusedException"/> for an error-status, and <see cref="DaenetIp2ProtocolException"/> for
    /// anything else that isn't the answer.
    /// </summary>
    public static int Value(DaenetIp2Message answer, DaenetIp2Request request)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(request);
        if (answer.Version != DaenetIp2Api.Version1)
        {
            throw Unreadable($"The answer is for SNMP version field {Number(answer.Version)}, not {Number(DaenetIp2Api.Version1)} (SNMPv1).");
        }

        if (answer.PduType != DaenetIp2Api.GetResponse)
        {
            throw Unreadable($"The answer's PDU has type {answer.PduType:X2}, not a GetResponse.");
        }

        if (answer.RequestId != request.RequestId)
        {
            throw Unreadable($"The answer is for request {Number(answer.RequestId)}, not {Number(request.RequestId)}.");
        }

        if (!answer.HasCommunity(request.Community))
        {
            throw Unreadable("The answer carries another community.");
        }

        if (answer.ErrorIndex is < 0 or > 1)
        {
            throw Unreadable($"The answer's error-index {Number(answer.ErrorIndex)} isn't 0 or 1: there is one variable.");
        }

        if (answer.ErrorStatus != DaenetIp2Api.NoError)
        {
            throw new DaenetIp2RefusedException(answer.ErrorStatus, answer.ErrorIndex);
        }

        if (!answer.OidBytes.SequenceEqual(request.OidBytes))
        {
            throw Unreadable($"The answer is for {OidText(answer.Oid)}, not {OidText(request.Oid)}.");
        }

        if (answer.ValueType != DaenetIp2Api.IntegerTag || answer.Value is not { } value)
        {
            throw Unreadable($"The answer's value has type {answer.ValueType:X2}, not INTEGER.");
        }

        // A port's byte may come as one raw byte, 02 01 FF, as well as BER's 02 02 00 FF: one byte is read unsigned.
        if (answer.ValueLength == 1 && value < 0)
        {
            value += 256;
        }

        if (value < 0 || value > request.MaxValue)
        {
            throw Unreadable($"The answer's value {Number(value)} isn't from 0 to {Number(request.MaxValue)}.");
        }

        if (request.Value is { } set && value != set)
        {
            throw Unreadable($"The answer doesn't confirm what was set: {Number(value)}, not {Number(set)}.");
        }

        return value;
    }

    /// <summary>The OID in the dotted form snmpget takes: ".1.3.6.1.4.1.42505.9.2.2.33.0".</summary>
    public static string OidText(IReadOnlyList<int> oid)
    {
        ArgumentNullException.ThrowIfNull(oid);
        return string.Concat(oid.Select(arc => "." + Number(arc)));
    }

    /// <summary>The bytes as the Debug screen shows them: "A0 1E 02 01 01".</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(bytes) is { Length: > 0 } hex
            ? string.Join(' ', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)))
            : "";
    }

    /// <summary>The OID's content bytes in BER: the first two arcs as 40 × a + b, each arc in base 128.</summary>
    public static byte[] Oid(IReadOnlyList<int> arcs)
    {
        ArgumentNullException.ThrowIfNull(arcs);
        if (arcs.Count is < 2 or > MaxOidArcs || arcs[0] is < 0 or > 2 || arcs[1] is < 0 or > int.MaxValue - 80 || (arcs[0] < 2 && arcs[1] >= 40) || arcs.Any(a => a < 0))
        {
            throw new ArgumentException("That isn't an OID the codec writes.", nameof(arcs));
        }

        List<byte> bytes = [];
        AddArc(bytes, (40 * arcs[0]) + arcs[1]);
        foreach (var arc in arcs.Skip(2))
        {
            AddArc(bytes, arc);
        }

        return [.. bytes];
    }

    // The one place a request the app sends is made: only from the closed list, never one of the OIDs that change the
    // board by a read (DaenetIp2Oid.Forbidden), checked once more here.
    private static DaenetIp2Request Request(byte pduType, int requestId, string community, DaenetIp2Oid oid, int? value)
    {
        if (!DaenetIp2Oid.All.Contains(oid) || DaenetIp2Oid.IsForbidden(oid.Arcs))
        {
            throw new InvalidOperationException($"{oid} isn't an OID the app may send.");
        }

        var communityBytes = CommunityBytes(community);
        var oidBytes = Oid(oid.Arcs);
        var valueTlv = value is { } v ? Integer(v) : [DaenetIp2Api.NullTag, 0];
        var message = Message(pduType, requestId, communityBytes, DaenetIp2Api.NoError, 0, oidBytes, valueTlv, out var pduOffset);
        return new DaenetIp2Request(message, pduOffset, pduType, requestId, communityBytes, oid, oidBytes, value);
    }

    // The community as its bytes: printable ASCII without spaces, as the board takes it, or none (DaenetIp2Models.CheckSendable).
    private static byte[] CommunityBytes(string community)
    {
        ArgumentNullException.ThrowIfNull(community);
        if (DaenetIp2Models.CheckSendable(community) is { } problem)
        {
            throw new ArgumentException(problem.ToString(), nameof(community));
        }

        return Encoding.ASCII.GetBytes(community);
    }

    private static byte[] Message(byte pduType, int requestId, byte[] community, int errorStatus, int errorIndex, byte[] oid, byte[] valueTlv, out int pduOffset)
    {
        byte[] binding = Tlv(DaenetIp2Api.SequenceTag, [.. Tlv(DaenetIp2Api.ObjectIdentifierTag, oid), .. valueTlv]);
        byte[] pdu = Tlv(pduType, [.. Integer(requestId), .. Integer(errorStatus), .. Integer(errorIndex), .. Tlv(DaenetIp2Api.SequenceTag, binding)]);
        byte[] head = [.. Integer(DaenetIp2Api.Version1), .. Tlv(DaenetIp2Api.OctetStringTag, community)];
        var message = Tlv(DaenetIp2Api.SequenceTag, [.. head, .. pdu]);
        if (message.Length > DaenetIp2Api.MaxMessageBytes)
        {
            throw new ArgumentException("The message would be longer than an SNMP message may be.");
        }

        pduOffset = message.Length - pdu.Length;
        return message;
    }

    private static byte[] Tlv(byte tag, ReadOnlySpan<byte> content)
    {
        byte[] length = content.Length switch
        {
            < 0x80 => [(byte)content.Length],
            <= 0xFF => [0x81, (byte)content.Length],
            _ => [0x82, (byte)(content.Length >> 8), (byte)content.Length],
        };
        return [tag, .. length, .. content];
    }

    // An INTEGER in the fewest bytes of two's complement.
    private static byte[] Integer(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        var start = 0;
        while (start < 3 && ((bytes[start] == 0x00 && (bytes[start + 1] & 0x80) == 0) || (bytes[start] == 0xFF && (bytes[start + 1] & 0x80) != 0)))
        {
            start++;
        }

        return Tlv(DaenetIp2Api.IntegerTag, bytes[start..]);
    }

    private static void AddArc(List<byte> bytes, int arc)
    {
        Span<byte> groups = stackalloc byte[MaxArcBytes];
        var count = 0;
        var rest = (uint)arc;
        do
        {
            groups[count++] = (byte)(rest & 0x7F);
            rest >>= 7;
        }
        while (rest != 0);

        for (var i = count - 1; i >= 0; i--)
        {
            bytes.Add((byte)(groups[i] | (i > 0 ? 0x80 : 0)));
        }
    }

    // An INTEGER of 1 to 4 bytes, two's complement.
    private static int ReadInt32(ReadOnlySpan<byte> content, string what)
    {
        if (content.Length is < 1 or > 4)
        {
            throw Unreadable($"{what} is an INTEGER of {Number(content.Length)} bytes, not 1 to 4.");
        }

        var value = (sbyte)content[0];
        var result = (int)value;
        foreach (var b in content[1..])
        {
            result = (result << 8) | b;
        }

        return result;
    }

    private static int[] DecodeOid(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            throw Unreadable("The OID is empty.");
        }

        List<int> arcs = [];
        var i = 0;
        while (i < content.Length)
        {
            if (arcs.Count >= MaxOidArcs)
            {
                throw Unreadable($"The OID has more than {Number(MaxOidArcs)} arcs.");
            }

            // No arc starts with a padding byte (0x80), and none is longer than an int.
            if (content[i] == 0x80)
            {
                throw Unreadable("The OID has an arc with a leading zero.");
            }

            long arc = 0;
            var length = 0;
            byte b;
            do
            {
                if (i >= content.Length)
                {
                    throw Unreadable("The OID's last arc is cut short.");
                }

                if (++length > MaxArcBytes)
                {
                    throw Unreadable("The OID has an arc too long to be one.");
                }

                b = content[i++];
                arc = (arc << 7) | (uint)(b & 0x7F);
            }
            while ((b & 0x80) != 0);

            if (arc > int.MaxValue)
            {
                throw Unreadable("The OID has an arc too large to be one.");
            }

            if (arcs.Count == 0)
            {
                // The first two arcs share the first: 40 × a + b.
                var first = arc < 40 ? 0 : arc < 80 ? 1 : 2;
                arcs.Add(first);
                arcs.Add((int)(arc - (40 * first)));
            }
            else
            {
                arcs.Add((int)arc);
            }
        }

        return [.. arcs];
    }

    private static DaenetIp2ProtocolException Unreadable(string message)
    {
        return new DaenetIp2ProtocolException(message);
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    // Reads BER TLVs from a span, never past it.
    private ref struct Reader
    {
        private ReadOnlySpan<byte> _rest;

        public Reader(ReadOnlySpan<byte> data)
        {
            _rest = data;
        }

        public readonly bool IsEmpty => _rest.IsEmpty;

        public readonly int Remaining => _rest.Length;

        public ReadOnlySpan<byte> Read(byte tag, string what)
        {
            var content = ReadAny(what, out var actual);
            if (actual != tag)
            {
                throw Unreadable($"{what} has type {actual:X2}, not {tag:X2}.");
            }

            return content;
        }

        public ReadOnlySpan<byte> ReadAny(string what, out byte tag)
        {
            if (_rest.Length < 2)
            {
                throw Unreadable($"{what} is cut short.");
            }

            tag = _rest[0];
            int length;
            int header;
            switch (_rest[1])
            {
                case < 0x80:
                    length = _rest[1];
                    header = 2;
                    break;
                case 0x81 when _rest.Length >= 3:
                    length = _rest[2];
                    header = 3;
                    break;
                case 0x82 when _rest.Length >= 4:
                    length = (_rest[2] << 8) | _rest[3];
                    header = 4;
                    break;
                case 0x81 or 0x82:
                    throw Unreadable($"{what} is cut short.");
                default:
                    throw Unreadable($"{what} has a length form the codec doesn't take ({_rest[1]:X2}).");
            }

            if (length > _rest.Length - header)
            {
                throw Unreadable($"{what} is cut short.");
            }

            var content = _rest.Slice(header, length);
            _rest = _rest[(header + length)..];
            return content;
        }

        public readonly void End(string what)
        {
            if (!_rest.IsEmpty)
            {
                throw Unreadable($"{what} has {Number(_rest.Length)} bytes too many.");
            }
        }
    }
}
