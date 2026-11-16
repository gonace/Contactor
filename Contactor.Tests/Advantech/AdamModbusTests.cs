using Contactor.Boards.Advantech;

namespace Contactor.Tests.Advantech;

/// <summary>The Modbus TCP codec against the frames in Advantech's ADAM-6000 manual, and answers that aren't the answer.</summary>
public sealed class AdamModbusTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Should_BuildTheManualsFrames_ByteForByte()
    {
        // Figure B.1: the whole frame, "command head" and "command body", reading 40001~40002 of an ADAM-6017 (FC 04).
        Assert.Equal("00 00 00 00 00 06 01 04 00 01 00 02", AdamModbus.Hex(AdamModbus.Frame(0, 1, Bytes("04 00 01 00 02"))));

        // Section 7.3.2: the command bodies of FC 01, FC 02 and FC 05, after the same command head. The addresses are
        // the manual's as it writes them; the app's own are below.
        Assert.Equal("00 00 00 00 00 06 01 01 00 17 00 08", AdamModbus.Hex(AdamModbus.Read(0, 1, AdamApi.ReadCoils, 0x0017, 8).Frame));
        Assert.Equal("00 00 00 00 00 06 01 02 00 01 00 08", AdamModbus.Hex(AdamModbus.Read(0, 1, AdamApi.ReadDiscreteInputs, 0x0001, 8).Frame));
        Assert.Equal("00 00 00 00 00 06 01 05 00 03 FF 00", AdamModbus.Hex(AdamModbus.WriteCoil(0, 1, 0x0003, on: true).Frame));
    }

    [Fact]
    public void Should_BuildTheAppsRequests_ByteForByte()
    {
        // Relay 1 is coil 00017, frame address 0x0010; input 1 is 00001, frame address 0x0000. Unit 1.
        Assert.Equal("00 07 00 00 00 06 01 01 00 10 00 06", AdamModbus.Hex(AdamModbus.ReadRelays(7, 6).Frame));
        Assert.Equal("00 08 00 00 00 06 01 02 00 00 00 06", AdamModbus.Hex(AdamModbus.ReadInputs(8, 6).Frame));
        Assert.Equal("01 00 00 00 00 06 01 05 00 15 00 00", AdamModbus.Hex(AdamModbus.WriteSingleCoil(256, 6, on: false).Frame));
        Assert.Equal("FF FF 00 00 00 06 01 05 00 10 FF 00", AdamModbus.Hex(AdamModbus.WriteSingleCoil(ushort.MaxValue, 1, on: true).Frame));
        Assert.True(AdamModbus.ReadInputs(1, 6).IsRead);
        Assert.False(AdamModbus.WriteSingleCoil(1, 1, on: true).IsRead);
    }

    [Fact]
    public void Should_RefuseRelaysAndQuantitiesItCantAddress()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.WriteSingleCoil(1, 0, on: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.WriteSingleCoil(1, 65, on: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.ReadRelays(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.ReadInputs(1, 2001));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.Read(1, 1, AdamApi.WriteSingleCoil, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.Frame(1, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.Frame(1, 1, new byte[254]));
    }

    [Theory]
    [InlineData(AdamApi.ReadCoils, "01 01 01 42", 0b0100_0010UL)]
    [InlineData(AdamApi.ReadDiscreteInputs, "01 02 01 60", 0b0110_0000UL)]
    public async Task Should_ReadTheManualsAnswers(byte function, string body, ulong bits)
    {
        // Section 7.3.2: "Coils 2~7 are on" as 42, and the inputs as 60, behind the command head. Bit 0 is the first
        // asked for, as the FC 15 example there maps its bits: 42 is coils 2 and 7. The manual's words for 60,
        // "inputs 2 and 3", read the byte the other way round; the app takes Modbus's order, as for 42.
        var request = AdamModbus.Read(1, 1, function, 1, 8);

        var frame = await ReadAsync(request, "00 01 00 00 00 04 " + body);

        Assert.Equal(bits, AdamModbus.Bits(frame, 8));
    }

    [Fact]
    public async Task Should_ReadRelaySix_AsBitFive_AndDropThePadding()
    {
        var frame = await ReadAsync(AdamModbus.ReadRelays(1, 6), "00 01 00 00 00 04 01 01 01 21");
        Assert.Equal((1UL << 5) | 1UL, AdamModbus.Bits(frame, 6));

        // Six relays asked for, padding bits set anyway: they mean nothing.
        var padded = await ReadAsync(AdamModbus.ReadInputs(1, 6), "00 01 00 00 00 04 01 02 01 C5");
        Assert.Equal(0b00_0101UL, AdamModbus.Bits(padded, 6));
    }

    [Fact]
    public async Task Should_TakeTheEcho_AsTheAnswerToASwitch()
    {
        // Section 7.3.2: "The normal response is an echo of the query": 01 05 00 03 FF 00.
        var manual = AdamModbus.WriteCoil(2, 1, 0x0003, on: true);
        Assert.Equal(manual.Frame, await ReadAsync(manual, "00 02 00 00 00 06 01 05 00 03 FF 00"));

        var off = AdamModbus.WriteSingleCoil(3, 2, on: false);
        Assert.Equal(off.Frame, await ReadAsync(off, "00 03 00 00 00 06 01 05 00 11 00 00"));
    }

    [Fact]
    public async Task Should_ReadAnAnswer_ThatComesAByteAtATime()
    {
        using var stream = new TrickleStream(Bytes("00 09 00 00 00 04 01 01 01 04"));

        var frame = await AdamModbus.ReadAnswerAsync(stream, AdamModbus.ReadRelays(9, 6), Cancel);

        Assert.Equal(0b100UL, AdamModbus.Bits(frame, 6));
    }

    [Fact]
    public async Task Should_ReadTwoAnswers_ThatCameInOnePacket_OneAtATime()
    {
        using var stream = new MemoryStream(Bytes("00 01 00 00 00 04 01 01 01 01 00 02 00 00 00 06 01 05 00 11 FF 00"));

        var read = await AdamModbus.ReadAnswerAsync(stream, AdamModbus.ReadRelays(1, 6), Cancel);
        var echo = await AdamModbus.ReadAnswerAsync(stream, AdamModbus.WriteSingleCoil(2, 2, on: true), Cancel);

        Assert.Equal(1UL, AdamModbus.Bits(read, 6));
        Assert.Equal("00 02 00 00 00 06 01 05 00 11 FF 00", AdamModbus.Hex(echo));
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData("00 01 00 00 00 03 01 81 02", 2)]
    [InlineData("00 01 00 00 00 03 01 81 01", 1)]
    public async Task Should_ThrowRefused_ForAnExceptionAnswerToARead(string answer, byte code)
    {
        // The manual shows no exception answer; this is Modbus's own: the function with 0x80, then the code.
        var error = await Assert.ThrowsAsync<AdamRefusedException>(() => ReadAsync(AdamModbus.ReadRelays(1, 6), answer));

        Assert.Equal(code, error.Code);
        Assert.Equal(answer, AdamModbus.Hex([.. error.Frame]));
    }

    [Theory]
    [InlineData("00 02 00 00 00 03 01 85 02", 2)]
    [InlineData("00 02 00 00 00 03 01 85 03", 3)]
    [InlineData("00 02 00 00 00 03 01 85 04", 4)]
    public async Task Should_ThrowRefused_ForAnExceptionAnswerToASwitch(string answer, byte code)
    {
        var error = await Assert.ThrowsAsync<AdamRefusedException>(() => ReadAsync(AdamModbus.WriteSingleCoil(2, 2, on: true), answer));

        Assert.Equal(code, error.Code);
        Assert.Equal(answer, AdamModbus.Hex([.. error.Frame]));
    }

    [Fact]
    public async Task Should_SkipTheLateAnswerToARequestGivenUpOn_Once()
    {
        // The answer to transaction 1 comes first, then the answer to transaction 2, which is the one asked for.
        using var stream = new MemoryStream(Bytes("00 01 00 00 00 04 01 01 01 3F 00 02 00 00 00 06 01 05 00 10 FF 00"));
        HashSet<ushort> late = [1];
        List<string> skipped = [];

        var echo = await AdamModbus.ReadAnswerAsync(stream, AdamModbus.WriteSingleCoil(2, 1, on: true), Cancel, late, f => skipped.Add(AdamModbus.Hex(f)));

        Assert.Equal("00 02 00 00 00 06 01 05 00 10 FF 00", AdamModbus.Hex(echo));
        Assert.Equal(["00 01 00 00 00 04 01 01 01 3F"], skipped);
        Assert.Empty(late);
    }

    [Theory]
    [InlineData("00 01 00 00 00 04 01 01 01 3F", "The answer is for transaction 1, not 3.")]
    [InlineData("00 07 00 00 00 04 01 01 01 3F", "The answer is for transaction 7, not 3.")]
    [InlineData("00 02 00 01 00 04 01 01 01 3F", "The late answer to transaction 2 isn't a Modbus frame.")]
    [InlineData("00 02 00 00 FF FF 01 01", "The late answer to transaction 2 isn't a Modbus frame.")]
    public async Task Should_SkipOnlyAWholeLateAnswer_ToARequestGivenUpOn(string answers, string message)
    {
        // Only transaction 2 was given up on; transaction 1 was answered, and 7 was never sent.
        using var stream = new MemoryStream(Bytes(answers));

        var error = await Assert.ThrowsAsync<AdamProtocolException>(
            () => AdamModbus.ReadAnswerAsync(stream, AdamModbus.WriteSingleCoil(3, 1, on: true), Cancel, new HashSet<ushort> { 2 }));

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("00 02 00 00 00 04 01 01 01 00", "The answer is for transaction 2, not 1.")]
    [InlineData("00 01 00 01 00 04 01 01 01 00", "The answer is for protocol 1, not Modbus (0).")]
    [InlineData("00 01 00 00 00 04 00 01 01 00", "The answer came from unit 0, not 1.")]
    [InlineData("00 01 00 00 00 01 01 01", "The answer's length 1 isn't a Modbus length.")]
    [InlineData("00 01 00 00 FF FF 01 01", "The answer's length 65535 isn't a Modbus length.")]
    [InlineData("00 01 00 00 00 04 01 02 01 00", "The answer is for function 02, not 01.")]
    [InlineData("00 01 00 00 00 05 01 01 01 00 00", "The answer is 5 bytes long, not 4.")]
    [InlineData("00 01 00 00 00 FE 01 01 FB", "The answer is 254 bytes long, not 4.")]
    [InlineData("00 01 00 00 00 04 01 01 02 00", "The answer has 2 bytes of data, not 1.")]
    [InlineData("00 01 00 00 00 04 01 81 02 00", "The exception answer is 4 bytes long, not 3.")]
    public async Task Should_RefuseAnAnswerThatIsntTheAnswer(string answer, string message)
    {
        var error = await Assert.ThrowsAsync<AdamProtocolException>(() => ReadAsync(AdamModbus.ReadRelays(1, 6), answer));

        Assert.Equal(message, error.Message);
        Assert.NotEmpty(error.Received);
    }

    [Theory]
    [InlineData("00 02 00 00 00 06 01 05 00 12 FF 00")]
    [InlineData("00 02 00 00 00 06 01 05 00 11 00 00")]
    public async Task Should_RefuseAnEchoOfAnotherRequest(string answer)
    {
        var error = await Assert.ThrowsAsync<AdamProtocolException>(() => ReadAsync(AdamModbus.WriteSingleCoil(2, 2, on: true), answer));

        Assert.Equal("The answer doesn't confirm what was asked.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00 01 00 00")]
    [InlineData("00 01 00 00 00 04 01 01 01")]
    [InlineData("00 01 00 00 00 03 01 81")]
    public async Task Should_ThrowIo_WhenTheAnswerStopsShort(string answer)
    {
        await Assert.ThrowsAsync<IOException>(() => ReadAsync(AdamModbus.ReadRelays(1, 6), answer));
    }

    [Fact]
    public async Task Should_ReadNoMoreThanTheHeader_WhenItGivesAHugeLength()
    {
        // A megabyte waits behind a header that says 65535 bytes follow: refused after 8 bytes, nothing more read.
        using var stream = new MemoryStream([.. Bytes("00 01 00 00 FF FF 01 01"), .. new byte[1024 * 1024]]);

        await Assert.ThrowsAsync<AdamProtocolException>(() => AdamModbus.ReadAnswerAsync(stream, AdamModbus.ReadRelays(1, 6), Cancel));

        Assert.Equal(8, stream.Position);
    }

    [Fact]
    public void Should_RefuseBitsTheAnswerDoesntCarry()
    {
        Assert.Throws<AdamProtocolException>(() => AdamModbus.Bits(Bytes("00 01 00 00 00 04 01 01 01 05"), 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.Bits(Bytes("00 01 00 00 00 04 01 01 01 05"), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AdamModbus.Bits(Bytes("00 01 00 00 00 04 01 01 01 05"), 65));
    }

    internal static byte[] Bytes(string hex)
    {
        return hex.Length == 0 ? [] : Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadAsync(AdamRequest request, string answer)
    {
        using var stream = new MemoryStream(Bytes(answer));
        return await AdamModbus.ReadAnswerAsync(stream, request, Cancel);
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
