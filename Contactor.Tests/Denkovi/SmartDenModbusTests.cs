using Contactor.Boards.Denkovi;

namespace Contactor.Tests.Denkovi;

/// <summary>The Modbus TCP codec against the frames in Denkovi's manual, and answers that aren't the answer.</summary>
public sealed class SmartDenModbusTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_BuildTheManualsRequests_ByteForByte()
    {
        // Tables 10.8, 10.9, 10.13 and 10.24 of the manual.
        Assert.Equal("00 01 00 00 00 06 00 01 00 00 00 03", SmartDenModbus.Hex(SmartDenModbus.ReadCoils(1, 0, 3).Frame));
        Assert.Equal("00 01 00 00 00 06 00 01 00 03 00 0D", SmartDenModbus.Hex(SmartDenModbus.ReadCoils(1, 3, 13).Frame));
        Assert.Equal("00 02 00 00 00 06 00 05 00 01 FF 00", SmartDenModbus.Hex(SmartDenModbus.WriteSingleCoil(2, 2, on: true).Frame));
        Assert.Equal("00 05 00 00 00 06 00 06 00 09 00 1E", SmartDenModbus.Hex(SmartDenModbus.Pulse(5, 10, TimeSpan.FromSeconds(3)).Frame));
    }

    [Fact]
    public void Should_BuildTheAppsRequests_ByteForByte()
    {
        Assert.Equal("00 07 00 00 00 06 00 01 00 00 00 10", SmartDenModbus.Hex(SmartDenModbus.ReadCoils(7, 0, 16).Frame));
        Assert.Equal("01 00 00 00 00 06 00 05 00 0F 00 00", SmartDenModbus.Hex(SmartDenModbus.WriteSingleCoil(256, 16, on: false).Frame));
        Assert.Equal("FF FF 00 00 00 06 00 05 00 00 FF 00", SmartDenModbus.Hex(SmartDenModbus.WriteSingleCoil(ushort.MaxValue, 1, on: true).Frame));
    }

    [Theory]
    [InlineData(1, 100, "00 01 00 00 00 06 00 06 00 00 00 01")]
    [InlineData(16, 700, "00 01 00 00 00 06 00 06 00 0F 00 07")]
    [InlineData(1, 6_553_500, "00 01 00 00 00 06 00 06 00 00 FF FF")]
    public void Should_BuildAPulse_InTenthsOfASecond(int relay, int milliseconds, string frame)
    {
        var request = SmartDenModbus.Pulse(1, relay, TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal(frame, SmartDenModbus.Hex(request.Frame));
        Assert.Equal(SmartDenApi.WriteSingleRegister, request.Function);
        Assert.False(request.IsRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(50)]
    [InlineData(150)]
    [InlineData(6_553_600)]
    [InlineData(int.MaxValue)]
    public void Should_RefuseAPulseLengthTheBoardCantDo(int milliseconds)
    {
        Assert.False(SmartDenModels.Pulse.CanDo(TimeSpan.FromMilliseconds(milliseconds)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.Pulse(1, 1, TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Should_RefuseRelaysAndQuantitiesItCantAddress()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.WriteSingleCoil(1, 0, on: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.WriteSingleCoil(1, 17, on: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.Pulse(1, 17, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.ReadCoils(1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.ReadCoils(1, 0, 2001));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.Frame(1, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.Frame(1, 0, new byte[254]));
    }

    [Theory]
    [InlineData("00 01 00 00 00 04 00 01 01 05", 0, 3, 0b101UL)]
    [InlineData("00 01 00 00 00 05 00 01 02 59 0B", 3, 13, 0x0B59UL)]
    public async Task Should_ReadTheManualsRelayAnswers(string answer, ushort address, ushort count, ulong relays)
    {
        // Tables 10.8 and 10.10, and tables 10.9 and 10.11, with 10.11's length 4 put right: the unit, the function,
        // the count and 2 bytes are 5. Bit 0 is the first relay asked for: relay 1, or relay 4.
        var request = SmartDenModbus.ReadCoils(1, address, count);

        var frame = await ReadAsync(request, answer);

        Assert.Equal(relays, SmartDenModbus.Bits(frame, count));
    }

    [Fact]
    public async Task Should_ReadRelaySixteen_AsBitFifteen_AndDropThePadding()
    {
        var frame = await ReadAsync(SmartDenModbus.ReadCoils(1, 0, 16), "00 01 00 00 00 05 00 01 02 01 80");
        Assert.Equal((1UL << 15) | 1UL, SmartDenModbus.Bits(frame, 16));

        // Three coils asked for, padding bits set anyway: they mean nothing.
        var padded = await ReadAsync(SmartDenModbus.ReadCoils(1, 0, 3), "00 01 00 00 00 04 00 01 01 FD");
        Assert.Equal(0b101UL, SmartDenModbus.Bits(padded, 3));
    }

    [Fact]
    public async Task Should_TakeTheEcho_AsTheAnswerToASwitchOrAPulse()
    {
        var on = SmartDenModbus.WriteSingleCoil(2, 2, on: true);
        Assert.Equal(on.Frame, await ReadAsync(on, "00 02 00 00 00 06 00 05 00 01 FF 00"));

        var pulse = SmartDenModbus.Pulse(5, 10, TimeSpan.FromSeconds(3));
        Assert.Equal(pulse.Frame, await ReadAsync(pulse, "00 05 00 00 00 06 00 06 00 09 00 1E"));
    }

    [Fact]
    public async Task Should_ReadAnAnswer_ThatComesAByteAtATime()
    {
        using var stream = new TrickleStream(Bytes("00 09 00 00 00 05 00 01 02 04 00"));

        var frame = await SmartDenModbus.ReadAnswerAsync(stream, SmartDenModbus.ReadCoils(9, 0, 16), Cancel);

        Assert.Equal(0b100UL, SmartDenModbus.Bits(frame, 16));
    }

    [Fact]
    public async Task Should_ReadTwoAnswers_ThatCameInOnePacket_OneAtATime()
    {
        using var stream = new MemoryStream(Bytes("00 01 00 00 00 05 00 01 02 01 00 00 02 00 00 00 06 00 05 00 01 FF 00"));

        var read = await SmartDenModbus.ReadAnswerAsync(stream, SmartDenModbus.ReadCoils(1, 0, 16), Cancel);
        var echo = await SmartDenModbus.ReadAnswerAsync(stream, SmartDenModbus.WriteSingleCoil(2, 2, on: true), Cancel);

        Assert.Equal(1UL, SmartDenModbus.Bits(read, 16));
        Assert.Equal("00 02 00 00 00 06 00 05 00 01 FF 00", SmartDenModbus.Hex(echo));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData("00 01 00 00 00 03 00 81 02", 2)]
    [InlineData("00 01 00 00 00 03 00 81 01", 1)]
    public async Task Should_ThrowRefused_ForAnExceptionAnswer(string answer, byte code)
    {
        // Table 10.12.
        var error = await Assert.ThrowsAsync<SmartDenRefusedException>(() => ReadAsync(SmartDenModbus.ReadCoils(1, 0, 16), answer));

        Assert.Equal(code, error.Code);
        Assert.Equal(answer, SmartDenModbus.Hex([.. error.Frame]));
    }

    [Theory]
    [InlineData("00 02 00 00 00 03 00 85 01", 1)]
    [InlineData("00 02 00 00 00 03 00 85 02", 2)]
    [InlineData("00 02 00 00 00 03 00 85 03", 3)]
    public async Task Should_ThrowRefused_ForTheManualsExceptionAnswerToASwitch(string answer, byte code)
    {
        // Table 10.15, to table 10.13's request.
        var error = await Assert.ThrowsAsync<SmartDenRefusedException>(() => ReadAsync(SmartDenModbus.WriteSingleCoil(2, 2, on: true), answer));

        Assert.Equal(code, error.Code);
        Assert.Equal(answer, SmartDenModbus.Hex([.. error.Frame]));
    }

    [Theory]
    [InlineData("00 05 00 00 00 03 00 86 01", 1)]
    [InlineData("00 05 00 00 00 03 00 86 02", 2)]
    [InlineData("00 05 00 00 00 03 00 86 03", 3)]
    public async Task Should_ThrowRefused_ForTheManualsExceptionAnswerToAPulse(string answer, byte code)
    {
        // Table 10.26, to table 10.24's request.
        var error = await Assert.ThrowsAsync<SmartDenRefusedException>(() => ReadAsync(SmartDenModbus.Pulse(5, 10, TimeSpan.FromSeconds(3)), answer));

        Assert.Equal(code, error.Code);
        Assert.Equal(answer, SmartDenModbus.Hex([.. error.Frame]));
    }

    [Fact]
    public async Task Should_SkipTheLateAnswerToARequestGivenUpOn_Once()
    {
        // The answer to transaction 1 comes first, then the answer to transaction 2, which is the one asked for.
        using var stream = new MemoryStream(Bytes("00 01 00 00 00 05 00 01 02 FF FF 00 02 00 00 00 06 00 05 00 00 FF 00"));
        HashSet<ushort> late = [1];
        List<string> skipped = [];

        var echo = await SmartDenModbus.ReadAnswerAsync(stream, SmartDenModbus.WriteSingleCoil(2, 1, on: true), Cancel, late, f => skipped.Add(SmartDenModbus.Hex(f)));

        Assert.Equal("00 02 00 00 00 06 00 05 00 00 FF 00", SmartDenModbus.Hex(echo));
        Assert.Equal(["00 01 00 00 00 05 00 01 02 FF FF"], skipped);
        Assert.Empty(late);
    }

    [Theory]
    [InlineData("00 01 00 00 00 05 00 01 02 FF FF", "The answer is for transaction 1, not 3.")]
    [InlineData("00 07 00 00 00 05 00 01 02 FF FF", "The answer is for transaction 7, not 3.")]
    [InlineData("00 02 00 01 00 05 00 01 02 FF FF", "The late answer to transaction 2 isn't a Modbus frame.")]
    [InlineData("00 02 00 00 FF FF 00 01", "The late answer to transaction 2 isn't a Modbus frame.")]
    public async Task Should_SkipOnlyAWholeLateAnswer_ToARequestGivenUpOn(string answers, string message)
    {
        // Only transaction 2 was given up on; transaction 1 was answered, and 7 was never sent.
        using var stream = new MemoryStream(Bytes(answers));

        var error = await Assert.ThrowsAsync<SmartDenProtocolException>(
            () => SmartDenModbus.ReadAnswerAsync(stream, SmartDenModbus.WriteSingleCoil(3, 1, on: true), Cancel, new HashSet<ushort> { 2 }));

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("00 02 00 00 00 05 00 01 02 00 00", "The answer is for transaction 2, not 1.")]
    [InlineData("00 01 00 01 00 05 00 01 02 00 00", "The answer is for protocol 1, not Modbus (0).")]
    [InlineData("00 01 00 00 00 05 01 01 02 00 00", "The answer came from unit 1, not 0.")]
    [InlineData("00 01 00 00 00 01 00 01", "The answer's length 1 isn't a Modbus length.")]
    [InlineData("00 01 00 00 FF FF 00 01", "The answer's length 65535 isn't a Modbus length.")]
    [InlineData("00 01 00 00 00 05 00 03 02 00 00", "The answer is for function 03, not 01.")]
    [InlineData("00 01 00 00 00 04 00 01 01 00", "The answer is 4 bytes long, not 5.")]
    [InlineData("00 01 00 00 00 FE 00 01 FB", "The answer is 254 bytes long, not 5.")]
    [InlineData("00 01 00 00 00 05 00 01 03 00 00", "The answer has 3 bytes of data, not 2.")]
    [InlineData("00 01 00 00 00 04 00 81 02 00", "The exception answer is 4 bytes long, not 3.")]
    public async Task Should_RefuseAnAnswerThatIsntTheAnswer(string answer, string message)
    {
        var error = await Assert.ThrowsAsync<SmartDenProtocolException>(() => ReadAsync(SmartDenModbus.ReadCoils(1, 0, 16), answer));

        Assert.Equal(message, error.Message);
        Assert.NotEmpty(error.Received);
    }

    [Theory]
    [InlineData("00 02 00 00 00 06 00 05 00 02 FF 00")]
    [InlineData("00 02 00 00 00 06 00 05 00 01 00 00")]
    public async Task Should_RefuseAnEchoOfAnotherRequest(string answer)
    {
        var error = await Assert.ThrowsAsync<SmartDenProtocolException>(() => ReadAsync(SmartDenModbus.WriteSingleCoil(2, 2, on: true), answer));

        Assert.Equal("The answer doesn't confirm what was asked.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00 01 00 00")]
    [InlineData("00 01 00 00 00 05 00 01 02 00")]
    [InlineData("00 01 00 00 00 03 00 81")]
    public async Task Should_ThrowIo_WhenTheAnswerStopsShort(string answer)
    {
        await Assert.ThrowsAsync<IOException>(() => ReadAsync(SmartDenModbus.ReadCoils(1, 0, 16), answer));
    }

    [Fact]
    public async Task Should_ReadNoMoreThanTheHeader_WhenItGivesAHugeLength()
    {
        // A megabyte waits behind a header that says 65535 bytes follow: refused after 8 bytes, nothing more read.
        using var stream = new MemoryStream([.. Bytes("00 01 00 00 FF FF 00 01"), .. new byte[1024 * 1024]]);

        await Assert.ThrowsAsync<SmartDenProtocolException>(() => SmartDenModbus.ReadAnswerAsync(stream, SmartDenModbus.ReadCoils(1, 0, 16), Cancel));

        Assert.Equal(8, stream.Position);
    }

    [Fact]
    public void Should_RefuseBitsTheAnswerDoesntCarry()
    {
        Assert.Throws<SmartDenProtocolException>(() => SmartDenModbus.Bits(Bytes("00 01 00 00 00 04 00 01 01 05"), 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.Bits(Bytes("00 01 00 00 00 04 00 01 01 05"), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartDenModbus.Bits(Bytes("00 01 00 00 00 04 00 01 01 05"), 65));
    }

    internal static byte[] Bytes(string hex)
    {
        return hex.Length == 0 ? [] : Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadAsync(SmartDenRequest request, string answer)
    {
        using var stream = new MemoryStream(Bytes(answer));
        return await SmartDenModbus.ReadAnswerAsync(stream, request, Cancel);
    }

    /// <summary>A stream that gives at most one byte per read, as a TCP answer split into many packets.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        public TrickleStream(byte[] bytes)
            : base(bytes)
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, 1));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        }
    }
}
