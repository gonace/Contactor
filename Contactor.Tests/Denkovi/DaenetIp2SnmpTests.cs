using Contactor.Boards.Denkovi;

namespace Contactor.Tests.Denkovi;

/// <summary>
/// The DAEnetIP2 v2's SNMPv1 codec, byte for byte. The manual gives commands, not bytes: these frames are its own
/// snmpget and snmpset examples (section 14.2.3) written out in BER by hand, as RFC 1157 sends them.
/// </summary>
public sealed class DaenetIp2SnmpTests
{
    // snmpget -v1 -c 000000000000 192.168.1.100 .1.3.6.1.4.1.42505.9.2.2.33.0 ("Get the whole P5"), request id 1.
    internal const string GetP5 =
        "30 31 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A0 1E 02 01 01 02 01 00 02 01 00 30 13 30 11 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 05 00";

    // snmpset -v1 -c private 192.168.1.100 .1.3.6.1.4.1.42505.9.2.2.1.0 i 1 (the manual's ON.bat), request id 2.
    internal const string SetP5Pin1High =
        "30 2D 02 01 00 04 07 70 72 69 76 61 74 65 A3 1F 02 01 02 02 01 00 02 01 00 30 14 30 12 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 01 00 02 01 01";

    // The answer to GetP5: P5 = 5, pins 1 and 3 high.
    internal const string P5Is5 =
        "30 32 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A2 1F 02 01 01 02 01 00 02 01 00 30 14 30 12 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 02 01 05";

    // The answer to GetP5: P5 = 255, an INTEGER of two bytes.
    internal const string P5Is255 =
        "30 33 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A2 20 02 01 01 02 01 00 02 01 00 30 15 30 13 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 02 02 00 FF";

    // The answer to SetP5Pin1High: the value echoed.
    internal const string SetP5Pin1HighDone =
        "30 2D 02 01 00 04 07 70 72 69 76 61 74 65 A2 1F 02 01 02 02 01 00 02 01 00 30 14 30 12 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 01 00 02 01 01";

    // An error answer to GetP5: noSuchName (2), error-index 1, the variable echoed with NULL.
    internal const string P5NoSuchName =
        "30 31 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A2 1E 02 01 01 02 01 02 02 01 01 30 13 30 11 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 05 00";

    // snmpget -v1 -c 000000000000 192.168.1.100 .1.3.6.1.4.1.42505.9.2.3.1.0 ("Get P6.1 - This will read analog input 1
    // level"), request id 1.
    internal const string GetP6Pin1 =
        "30 31 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A0 1E 02 01 01 02 01 00 02 01 00 30 13 30 11 06 0D 2B 06 01 04 01 82 CC 09 09 02 03 01 00 05 00";

    // The answer to GetP6Pin1: 512, an INTEGER of two bytes.
    internal const string P6Pin1Is512 =
        "30 33 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A2 20 02 01 01 02 01 00 02 01 00 30 15 30 13 06 0D 2B 06 01 04 01 82 CC 09 09 02 03 01 00 02 02 02 00";

    private const string ReadOnly = DaenetIp2Api.FactoryReadOnlyCommunity;
    private const string ReadWrite = DaenetIp2Api.FactoryReadWriteCommunity;

    [Fact]
    public void Should_WriteTheManualsGetOfPortP5_ByteForByte()
    {
        var request = DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5);

        Assert.Equal(GetP5, DaenetIp2Snmp.Hex(request.Message));
        Assert.Equal(".1.3.6.1.4.1.42505.9.2.2.33.0", DaenetIp2Snmp.OidText(request.Oid));
        Assert.Equal(DaenetIp2Api.MaxPortValue, request.MaxValue);
        Assert.Null(request.Value);

        // The PDU alone, as the trace shows it: no community.
        Assert.Equal(GetP5[(GetP5.IndexOf("A0", StringComparison.Ordinal))..], DaenetIp2Snmp.Hex(request.Pdu));
    }

    [Fact]
    public void Should_WriteTheManualsSetOfPinP5Point1_ByteForByte()
    {
        var request = DaenetIp2Snmp.SetPin(2, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: true);

        Assert.Equal(SetP5Pin1High, DaenetIp2Snmp.Hex(request.Message));
        Assert.Equal(".1.3.6.1.4.1.42505.9.2.2.1.0", DaenetIp2Snmp.OidText(request.Oid));
        Assert.Equal(1, request.Value);
        Assert.Equal(1, request.MaxValue);
        Assert.Equal(
            "30 2D 02 01 00 04 07 70 72 69 76 61 74 65 A3 1F 02 01 03 02 01 00 02 01 00 30 14 30 12 06 0D 2B 06 01 04 01 82 CC 09 09 02 01 08 00 02 01 00",
            DaenetIp2Snmp.Hex(DaenetIp2Snmp.SetPin(3, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P3, 8), high: false).Message));
    }

    [Fact]
    public void Should_WriteTheManualsGetOfAnalogInputOne_ByteForByte()
    {
        var request = DaenetIp2Snmp.GetAnalog(1, ReadOnly, 1);

        Assert.Equal(GetP6Pin1, DaenetIp2Snmp.Hex(request.Message));
        Assert.Equal(".1.3.6.1.4.1.42505.9.2.3.1.0", DaenetIp2Snmp.OidText(request.Oid));
        Assert.Equal(".1.3.6.1.4.1.42505.9.2.3.8.0", DaenetIp2Snmp.OidText(DaenetIp2Snmp.GetAnalog(2, ReadOnly, 8).Oid));
        Assert.Equal(1023, request.MaxValue);
        Assert.Null(request.Value);
        Assert.Equal(DaenetIp2Api.GetRequest, request.PduType);
        Assert.Throws<ArgumentOutOfRangeException>(() => DaenetIp2Snmp.GetAnalog(1, ReadOnly, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DaenetIp2Snmp.GetAnalog(1, ReadOnly, 9));
    }

    [Fact]
    public void Should_KnowTheAnalogInputs_OnlyByTheOidsOfTable9Point6()
    {
        Assert.Equal(1, DaenetIp2Api.AnalogInputOf(DaenetIp2Api.AnalogOid(1)));
        Assert.Equal(8, DaenetIp2Api.AnalogInputOf(DaenetIp2Api.AnalogOid(8)));
        Assert.Null(DaenetIp2Api.AnalogInputOf([.. DaenetIp2Api.Enterprise, 9, 2, 3, 9, 0]));
        Assert.Null(DaenetIp2Api.AnalogInputOf([.. DaenetIp2Api.Enterprise, 9, 2, 3, 0, 0]));
        Assert.Null(DaenetIp2Api.AnalogInputOf([.. DaenetIp2Api.Enterprise, 9, 2, 3, 33, 0]));
        Assert.Null(DaenetIp2Api.AnalogInputOf([.. DaenetIp2Api.Enterprise, 9, 2, 3, 1, 1]));
        Assert.Null(DaenetIp2Api.AnalogInputOf([.. DaenetIp2Api.Enterprise, 9, 2, 2, 1, 0]));
        Assert.Null(DaenetIp2Api.AnalogInputOf([.. DaenetIp2Api.Enterprise, 9, 2, 3, 1]));
        Assert.Null(DaenetIp2Api.AnalogInputOf([1, 3, 6, 1, 4, 1, 42504, 9, 2, 3, 1, 0]));
    }

    [Theory]
    [InlineData(0, "02 01 00")]
    [InlineData(127, "02 01 7F")]
    [InlineData(128, "02 02 00 80")]
    [InlineData(512, "02 02 02 00")]
    [InlineData(1023, "02 02 03 FF")]
    public void Should_ReadTheAnswerToAnAnalogGet(int value, string tlv)
    {
        var request = DaenetIp2Snmp.GetAnalog(1, ReadOnly, 1);
        var answer = DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.AnalogOid(1), value);

        Assert.EndsWith(" 09 09 02 03 01 00 " + tlv, DaenetIp2Snmp.Hex(answer), StringComparison.Ordinal);
        Assert.Equal(value, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(answer), request));
    }

    [Fact]
    public void Should_ReadAnAnalogAnswer_ByteForByte_AlsoWithMoreBytesThanBerNeeds()
    {
        var request = DaenetIp2Snmp.GetAnalog(1, ReadOnly, 1);

        Assert.Equal(P6Pin1Is512, DaenetIp2Snmp.Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.AnalogOid(1), 512)));
        Assert.Equal(512, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(P6Pin1Is512)), request));

        // 1023 as an INTEGER of 4 bytes, 02 04 00 00 03 FF: two bytes longer, so every length around it too.
        const string fourBytes =
            "30 35 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A2 22 02 01 01 02 01 00 02 01 00 30 17 30 15 06 0D 2B 06 01 04 01 82 CC 09 09 02 03 01 00 02 04 00 00 03 FF";
        Assert.Equal(1023, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(fourBytes)), request));
    }

    public static TheoryData<string, string> WrongAnalogAnswers { get; } = new()
    {
        // Out of 0..1023: 1024, −1 in two bytes, and the largest INTEGER.
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.AnalogOid(1), 1024)), "The answer's value 1024 isn't from 0 to 1023." },
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.AnalogOid(1), -200)), "The answer's value -200 isn't from 0 to 1023." },
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.AnalogOid(1), int.MaxValue)), "The answer's value 2147483647 isn't from 0 to 1023." },

        // Not an INTEGER: a NULL, and an OCTET STRING "512".
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.AnalogOid(1), null)), "The answer's value has type 05, not INTEGER." },
        {
            "30 34 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A2 21 02 01 01 02 01 00 02 01 00 30 16 30 14 06 0D 2B 06 01 04 01 82 CC 09 09 02 03 01 00 04 03 35 31 32",
            "The answer's value has type 04, not INTEGER."
        },

        // Another input's value.
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.AnalogOid(2), 5)), "The answer is for .1.3.6.1.4.1.42505.9.2.3.2.0, not .1.3.6.1.4.1.42505.9.2.3.1.0." },
    };

    [Theory]
    [MemberData(nameof(WrongAnalogAnswers))]
    public void Should_RefuseAnAnalogAnswer_OutOfRangeOrOfAnotherType(string answer, string error)
    {
        var request = DaenetIp2Snmp.GetAnalog(1, ReadOnly, 1);

        var thrown = Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(answer)), request));

        Assert.Equal(error, thrown.Message);
    }

    [Fact]
    public void Should_ThrowRefused_ForAnAnalogGetsErrorStatus()
    {
        var request = DaenetIp2Snmp.GetAnalog(1, ReadOnly, 1);
        var answer = DaenetIp2Snmp.Response(1, ReadOnly, request.Oid, null, DaenetIp2Api.NoSuchName, 1);

        var refused = Assert.Throws<DaenetIp2RefusedException>(() => DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(answer), request));

        Assert.Equal(DaenetIp2Api.NoSuchName, refused.ErrorStatus);
    }

    [Theory]
    [InlineData(0, "02 01 00")]
    [InlineData(127, "02 01 7F")]
    [InlineData(128, "02 02 00 80")]
    [InlineData(255, "02 02 00 FF")]
    [InlineData(256, "02 02 01 00")]
    [InlineData(-1, "02 01 FF")]
    [InlineData(-129, "02 02 FF 7F")]
    [InlineData(int.MaxValue, "02 04 7F FF FF FF")]
    [InlineData(int.MinValue, "02 04 80 00 00 00")]
    public void Should_WriteAndReadARequestId_InTheFewestBytes(int id, string tlv)
    {
        var message = DaenetIp2Snmp.Get(id, "", DaenetIp2Oid.Port(DaenetIp2Port.P3)).Message;

        Assert.Equal("A0", DaenetIp2Snmp.Hex(message.AsSpan(4 + 3, 1)));
        Assert.StartsWith(tlv, DaenetIp2Snmp.Hex(message.AsSpan(9)), StringComparison.Ordinal);
        Assert.Equal(id, DaenetIp2Snmp.Decode(message).RequestId);
    }

    [Fact]
    public void Should_ReadTheManualsGet_AsTheSimulatorDoes()
    {
        var message = DaenetIp2Snmp.Decode(Bytes(GetP5));

        Assert.Equal(0, message.Version);
        Assert.True(message.HasCommunity(ReadOnly));
        Assert.False(message.HasCommunity(ReadWrite));
        Assert.Equal(DaenetIp2Api.GetRequest, message.PduType);
        Assert.Equal(1, message.RequestId);
        Assert.Equal(DaenetIp2Api.PortOid(DaenetIp2Port.P5), message.Oid);
        Assert.Equal(DaenetIp2Api.NullTag, message.ValueType);
        Assert.Null(message.Value);
    }

    [Theory]
    [InlineData(P5Is5, 5)]
    [InlineData(P5Is255, 255)]
    public void Should_ReadTheAnswerToAGet(string answer, int value)
    {
        var request = DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5);

        Assert.Equal(value, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(answer)), request));
        Assert.Equal(answer, DaenetIp2Snmp.Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), value)));
    }

    [Theory]
    [InlineData("02 01 05", 5)]
    [InlineData("02 01 7F", 127)]
    [InlineData("02 01 80", 128)]
    [InlineData("02 01 FF", 255)]
    public void Should_ReadAPortByteOfOneByte_AsUnsigned(string tlv, int value)
    {
        // Some agents send a byte as it is, 02 01 FF, not as BER's 02 02 00 FF: both are 255.
        var answer = P5Is5.Replace("02 01 05", tlv, StringComparison.Ordinal);

        Assert.Equal(value, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(answer)), DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5)));
    }

    [Fact]
    public void Should_StillRefuseAPinOfOneByteAbove1()
    {
        var request = DaenetIp2Snmp.Get(1, ReadOnly, DaenetIp2Oid.Pin(new DaenetIp2Pin(DaenetIp2Port.P5, 1)));
        var low = Hex(DaenetIp2Snmp.Response(1, ReadOnly, request.Oid, 0));
        var answer = DaenetIp2Snmp.Decode(Bytes(low[..^"02 01 00".Length] + "02 01 FF"));

        Assert.Equal("The answer's value 255 isn't from 0 to 1.", Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Value(answer, request)).Message);
    }

    [Fact]
    public void Should_ReadTheEchoOfASet()
    {
        var request = DaenetIp2Snmp.SetPin(2, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: true);

        Assert.Equal(1, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(SetP5Pin1HighDone)), request));
    }

    [Fact]
    public void Should_TakeTheLongFormOfALength()
    {
        // "30 32" written as "30 81 32": BER allows it, and some agents send it.
        var answer = Bytes("30 81 32" + P5Is5[5..]);

        Assert.Equal(5, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(answer), DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5)));
    }

    [Fact]
    public void Should_ThrowRefused_ForAnErrorStatus()
    {
        var request = DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5);

        var error = Assert.Throws<DaenetIp2RefusedException>(() => DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(P5NoSuchName)), request));

        Assert.Equal((2, 1), (error.ErrorStatus, error.ErrorIndex));
        Assert.Equal("The board refused it: no such name (error 2).", error.Message);
        Assert.Equal(P5NoSuchName, DaenetIp2Snmp.Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), null, DaenetIp2Api.NoSuchName, 1)));
    }

    public static TheoryData<string, string> WrongAnswers { get; } = new()
    {
        { Hex(DaenetIp2Snmp.Response(9, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), 5)), "The answer is for request 9, not 1." },
        { Hex(DaenetIp2Snmp.Response(1, "public", DaenetIp2Api.PortOid(DaenetIp2Port.P5), 5)), "The answer carries another community." },
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P3), 5)), "The answer is for .1.3.6.1.4.1.42505.9.2.1.33.0, not .1.3.6.1.4.1.42505.9.2.2.33.0." },
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), 256)), "The answer's value 256 isn't from 0 to 255." },
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), -129)), "The answer's value -129 isn't from 0 to 255." },
        { P5Is5.Replace("30 32", "30 33", StringComparison.Ordinal).Replace("A2 1F", "A2 20", StringComparison.Ordinal).Replace("30 14 30 12", "30 15 30 13", StringComparison.Ordinal).Replace("02 01 05", "02 02 FF FF", StringComparison.Ordinal), "The answer's value -1 isn't from 0 to 255." },
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), null)), "The answer's value has type 05, not INTEGER." },
        { Hex(DaenetIp2Snmp.Response(1, ReadOnly, DaenetIp2Api.PortOid(DaenetIp2Port.P5), null, DaenetIp2Api.NoSuchName, 2)), "The answer's error-index 2 isn't 0 or 1: there is one variable." },

        // Version field 1 (SNMPv2c), an OCTET STRING value, and the request itself coming back.
        { P5Is5.Replace("30 32 02 01 00", "30 32 02 01 01", StringComparison.Ordinal), "The answer is for SNMP version field 1, not 0 (SNMPv1)." },
        { P5Is5.Replace("21 00 02 01 05", "21 00 04 01 05", StringComparison.Ordinal), "The answer's value has type 04, not INTEGER." },
        { GetP5, "The answer's PDU has type A0, not a GetResponse." },
    };

    [Theory]
    [MemberData(nameof(WrongAnswers))]
    public void Should_RefuseAnAnswerThatIsntTheAnswer(string answer, string error)
    {
        var request = DaenetIp2Snmp.GetPort(1, ReadOnly, DaenetIp2Port.P5);

        var exception = Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(answer)), request));

        Assert.Equal(error, exception.Message);
    }

    [Fact]
    public void Should_RefuseAnEchoOfAnotherValue()
    {
        var request = DaenetIp2Snmp.SetPin(2, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 1), high: false);

        var error = Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(SetP5Pin1HighDone)), request));

        Assert.Equal("The answer doesn't confirm what was set: 1, not 0.", error.Message);
    }

    [Theory]
    [InlineData("", "The message is cut short.")]
    [InlineData("30", "The message is cut short.")]
    [InlineData("30 05 02 01 00", "The message is cut short.")]
    [InlineData("30 80 02 01 00 00 00", "The message has a length form the codec doesn't take (80).")]
    [InlineData("30 83 00 00 03 02 01 00", "The message has a length form the codec doesn't take (83).")]
    [InlineData("30 82 00", "The message is cut short.")]
    [InlineData("02 01 00", "The message has type 02, not 30.")]
    [InlineData("30 03 04 01 00", "The version has type 04, not 02.")]
    [InlineData("30 05 02 00 04 00 A0", "The version is an INTEGER of 0 bytes, not 1 to 4.")]
    [InlineData("30 0B 02 05 00 00 00 00 00 04 00 A0 00", "The version is an INTEGER of 5 bytes, not 1 to 4.")]
    [InlineData("30 07 02 01 00 04 00 A4 00", "The PDU has type A4, not a request or an answer.")]
    [InlineData("30 10 02 01 00 04 00 A0 09 02 01 01 02 01 00 02 01 00", "The variables is cut short.")]
    [InlineData("30 12 02 01 00 04 00 A0 0B 02 01 01 02 01 00 02 01 00 30 00", "The message has no variable.")]
    public void Should_RefuseADatagramThatIsntAnSnmpMessage(string datagram, string error)
    {
        var exception = Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(Bytes(datagram)));

        Assert.Equal(error, exception.Message);
    }

    [Fact]
    public void Should_RefuseTrailingBytes_AndTwoVariables()
    {
        Assert.Equal("The datagram has 1 bytes too many.", Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(Bytes(P5Is5 + " 00"))).Message);

        // Two variables: the board takes one OID per request.
        var binding = "30 11 06 0D 2B 06 01 04 01 82 CC 09 09 02 02 21 00 05 00";
        var two = $"30 44 02 01 00 04 0C 30 30 30 30 30 30 30 30 30 30 30 30 A0 31 02 01 01 02 01 00 02 01 00 30 26 {binding} {binding}";
        Assert.Equal("The message has more than one variable.", Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(Bytes(two))).Message);
    }

    [Theory]
    [InlineData("06 00", "The OID is empty.")]
    [InlineData("06 03 2B 80 01", "The OID has an arc with a leading zero.")]
    [InlineData("06 02 2B 86", "The OID's last arc is cut short.")]
    [InlineData("06 07 2B 81 81 81 81 81 01", "The OID has an arc too long to be one.")]
    [InlineData("06 06 2B 8F FF FF FF 7F", "The OID has an arc too large to be one.")]
    [InlineData("05 01 00", "The OID has type 05, not 06.")]
    public void Should_RefuseAnOidItCantRead(string oid, string error)
    {
        var exception = Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(Bytes(WithVariable(oid, "05 00"))));

        Assert.Equal(error, exception.Message);
    }

    [Fact]
    public void Should_RefuseANullWithContent_AndAValueTooLongForAnInteger()
    {
        var oid = "06 03 2B 06 01";

        Assert.Equal("The NULL value isn't empty.", Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(Bytes(WithVariable(oid, "05 01 00")))).Message);
        Assert.Equal(
            "The value is an INTEGER of 5 bytes, not 1 to 4.",
            Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(Bytes(WithVariable(oid, "02 05 00 FF FF FF FF")))).Message);
    }

    [Fact]
    public void Should_RefuseADatagramLongerThanAnSnmpMessage_WithoutReadingIt()
    {
        var huge = new byte[65_535];
        huge[0] = 0x30;

        Assert.Equal(
            "The datagram is 65535 bytes, more than an SNMP message of 484.",
            Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(huge)).Message);
        Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(new byte[DaenetIp2Api.MaxMessageBytes + 1]));
    }

    [Fact]
    public void Should_NeverReadPastTheDatagram_WhateverItHolds()
    {
        // Every cut of a good answer, and every byte of it changed: an answer or an unreadable one, never anything else.
        var answer = Bytes(P5Is5);
        for (var length = 0; length < answer.Length; length++)
        {
            Assert.Throws<DaenetIp2ProtocolException>(() => DaenetIp2Snmp.Decode(answer.AsSpan(0, length)));
        }

        for (var at = 0; at < answer.Length; at++)
        {
            foreach (var value in new byte[] { 0x00, 0x7F, 0x80, 0x81, 0x82, 0xFF })
            {
                var changed = (byte[])answer.Clone();
                changed[at] = value;
                try
                {
                    DaenetIp2Snmp.Decode(changed);
                }
                catch (DaenetIp2ProtocolException)
                {
                }
            }
        }
    }

    [Theory]
    [InlineData("thirteen-char")]
    [InlineData("abc")]
    [InlineData("with space")]
    [InlineData("åäö")]
    public void Should_RefuseACommunityTheBoardCantTake_BeforeAnythingIsSent(string community)
    {
        Assert.Throws<ArgumentException>(() => DaenetIp2Snmp.GetPort(1, community, DaenetIp2Port.P5));
    }

    [Fact]
    public void Should_SendAnEmptyCommunity_AsAnEmptyString()
    {
        Assert.StartsWith("30 25 02 01 00 04 00 A0", DaenetIp2Snmp.Hex(DaenetIp2Snmp.GetPort(1, "", DaenetIp2Port.P5).Message), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_RefuseAPinTheBoardHasNot()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DaenetIp2Snmp.SetPin(1, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 9), high: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => DaenetIp2Snmp.SetPin(1, ReadWrite, new DaenetIp2Pin(DaenetIp2Port.P5, 0), high: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => DaenetIp2Snmp.SetPin(1, ReadWrite, new DaenetIp2Pin((DaenetIp2Port)3, 1), high: true));
    }

    [Fact]
    public void Should_NeverShowTheCommunity_InARequestOrAMessage()
    {
        var request = DaenetIp2Snmp.GetPort(1, "s3cret!", DaenetIp2Port.P5);
        var message = DaenetIp2Snmp.Decode(request.Message);

        Assert.DoesNotContain("s3cret", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", message.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(DaenetIp2Snmp.Hex("s3cret"u8), DaenetIp2Snmp.Hex(request.Pdu), StringComparison.Ordinal);
        Assert.DoesNotContain(DaenetIp2Snmp.Hex("s3cret"u8), DaenetIp2Snmp.Hex(message.Pdu), StringComparison.Ordinal);
    }

    // Issue #515: a read of cfgReset restarts the board, one of cfgDefault loads its factory settings, and pctrlRestart
    // pulses the restart pin. The client's requests are built only from the closed list, which holds none of them, and
    // nothing can build a request from an OID given as data.
    [Fact]
    public void Should_NeverLetTheClientSend_cfgReset_cfgDefault_OrPctrlRestart()
    {
        int[] prefix = [1, 3, 6, 1, 4, 1, 42505];
        int[][] forbidden = [[.. prefix, 9, 1, 10, 0], [.. prefix, 9, 1, 30, 0], [.. prefix, 9, 2, 4], [.. prefix, 9, 2, 4, 0]];

        // The closed list: each OID once, none of these, nothing under them.
        Assert.Equal(DaenetIp2Oid.All.Count, DaenetIp2Oid.All.Select(o => DaenetIp2Snmp.OidText(o.Arcs)).Distinct().Count());
        Assert.All(forbidden, f => Assert.True(DaenetIp2Oid.IsForbidden(f)));
        Assert.All(DaenetIp2Oid.All, oid => Assert.False(DaenetIp2Oid.IsForbidden(oid.Arcs), oid.ToString()));
        Assert.All(DaenetIp2Oid.All, oid => Assert.DoesNotContain(forbidden, f => oid.Arcs.Take(f.Length).SequenceEqual(f)));

        // No way to make a request or a closed-list OID from data: no public constructor, and nothing that makes a
        // request takes an OID's arcs. Every request is a get or a set.
        var assembly = typeof(DaenetIp2Request).Assembly;
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        Assert.All(typeof(DaenetIp2Request).GetConstructors(Any), c => Assert.False(c.IsPublic));
        Assert.All(typeof(DaenetIp2Oid).GetConstructors(Any), c => Assert.True(c.IsPrivate));
        var makers = assembly.GetTypes().SelectMany(t => t.GetMethods(Any)).Where(m => m.ReturnType == typeof(DaenetIp2Request))
            .Concat<System.Reflection.MethodBase>(typeof(DaenetIp2Request).GetConstructors(Any)).ToList();
        Assert.NotEmpty(makers);
        Assert.All(makers, m => Assert.DoesNotContain(m.GetParameters(), p => typeof(IEnumerable<int>).IsAssignableFrom(p.ParameterType)));

        // Every request the codec makes, for every OID on the list, is a GetRequest or a SetRequest of that OID.
        foreach (var oid in DaenetIp2Oid.All)
        {
            var get = DaenetIp2Snmp.Decode(DaenetIp2Snmp.Get(1, ReadOnly, oid).Message);
            Assert.Equal(DaenetIp2Api.GetRequest, get.PduType);
            Assert.Equal(oid.Arcs, get.Oid);
            if (oid.Writable)
            {
                Assert.Equal(DaenetIp2Api.SetRequest, DaenetIp2Snmp.Decode(DaenetIp2Snmp.Set(1, "private", oid, 0).Message).PduType);
            }
            else
            {
                Assert.Throws<ArgumentException>(() => DaenetIp2Snmp.Set(1, "private", oid, 0));
            }
        }
    }

    // Table 9.1: cfgVersion x.9.1.8.0 INTEGER(0..65535), LSB=VER_MINOR, MSB=VER_MAJOR; cfgP5DupRST x.9.1.29.0
    // INTEGER(0..255), read-write.
    [Fact]
    public void Should_AskForTheFirmwareVersionAndTheRestartPins_AsTable9Point1Says()
    {
        Assert.Equal(".1.3.6.1.4.1.42505.9.1.8.0", DaenetIp2Snmp.OidText(DaenetIp2Oid.FirmwareVersion.Arcs));
        Assert.Equal(".1.3.6.1.4.1.42505.9.1.29.0", DaenetIp2Snmp.OidText(DaenetIp2Oid.RestartPins.Arcs));
        Assert.Equal((65535, false), (DaenetIp2Oid.FirmwareVersion.MaxValue, DaenetIp2Oid.FirmwareVersion.Writable));
        Assert.Equal((255, true), (DaenetIp2Oid.RestartPins.MaxValue, DaenetIp2Oid.RestartPins.Writable));
        Assert.Equal(0x0461, DaenetIp2Api.RestartPinsFirmware);
        Assert.Equal("4.097", DaenetIp2Api.FirmwareText(0x0461));

        // snmpset -v1 -c private … .1.3.6.1.4.1.42505.9.1.29.0 i 192 (P5.7 and P5.8), request id 7, and its echo.
        var set = DaenetIp2Snmp.SetRestartPins(7, "private", 0b1100_0000);
        Assert.Equal(
            "30 2D 02 01 00 04 07 70 72 69 76 61 74 65 A3 1F 02 01 07 02 01 00 02 01 00 30 14 30 12 06 0C 2B 06 01 04 01 82 CC 09 09 01 1D 00 02 02 00 C0",
            Hex(set.Message));
        var echo = DaenetIp2Snmp.Decode(DaenetIp2Snmp.Response(7, "private", DaenetIp2Oid.RestartPins.Arcs, 0b1100_0000));
        Assert.Equal(0b1100_0000, DaenetIp2Snmp.Value(echo, set));

        // Version 4.097 as an INTEGER of two bytes: 04 61.
        var get = DaenetIp2Snmp.GetFirmwareVersion(1, ReadOnly);
        var version = Hex(DaenetIp2Snmp.Response(1, ReadOnly, get.Oid, 0x0461));
        Assert.EndsWith("06 0C 2B 06 01 04 01 82 CC 09 09 01 08 00 02 02 04 61", version, StringComparison.Ordinal);
        Assert.Equal(0x0461, DaenetIp2Snmp.Value(DaenetIp2Snmp.Decode(Bytes(version)), get));

        Assert.Throws<ArgumentOutOfRangeException>(() => DaenetIp2Snmp.SetRestartPins(1, "private", 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => DaenetIp2Snmp.SetRestartPins(1, "private", -1));
    }

    internal static byte[] Bytes(string hex)
    {
        return hex.Length == 0 ? [] : Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
    }

    private static string Hex(byte[] bytes)
    {
        return DaenetIp2Snmp.Hex(bytes);
    }

    // A GetRequest with an empty community, request id 1 and this variable's OID and value TLVs.
    private static string WithVariable(string oid, string value)
    {
        var binding = Bytes($"{oid} {value}");
        var bindingTlv = new byte[] { 0x30, (byte)binding.Length }.Concat(binding).ToArray();
        var list = new byte[] { 0x30, (byte)bindingTlv.Length }.Concat(bindingTlv).ToArray();
        var pduContent = Bytes("02 01 01 02 01 00 02 01 00").Concat(list).ToArray();
        var pdu = new byte[] { 0xA0, (byte)pduContent.Length }.Concat(pduContent).ToArray();
        var content = Bytes("02 01 00 04 00").Concat(pdu).ToArray();
        return Hex([0x30, (byte)content.Length, .. content]);
    }
}
