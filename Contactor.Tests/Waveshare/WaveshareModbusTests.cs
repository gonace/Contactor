using Contactor.Boards.Waveshare;

namespace Contactor.Tests.Waveshare;

/// <summary>The Modbus RTU codec against the frames in Waveshare's wiki, and answers that aren't the answer.</summary>
public sealed class WaveshareModbusTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("01 01 00 00 00 08 3D CC")]
    [InlineData("01 01 01 01 90 48")]
    [InlineData("01 01 01 41 91 B8")]
    [InlineData("01 05 00 00 FF 00 8C 3A")]
    [InlineData("01 05 00 00 00 00 CD CA")]
    [InlineData("01 05 00 00 55 00 F2 9A")]
    [InlineData("01 05 00 FF FF 00 BC 0A")]
    [InlineData("01 0F 00 00 00 08 01 FF BE D5")]
    [InlineData("01 02 00 00 00 08 79 CC")]
    [InlineData("01 02 01 00 A1 88")]
    [InlineData("01 05 02 00 00 07 8D B0")]
    [InlineData("01 03 80 00 00 01 AD CA")]
    [InlineData("01 85 03 02 91")]
    [InlineData("01 0F 00 00 00 10 02 FF FF E3 90")]
    [InlineData("01 0F 00 00 00 1E 04 FF FF FF 3F C1 92")]
    public void Should_MatchTheWikisCrc(string frame)
    {
        var bytes = Bytes(frame);

        Assert.True(WaveshareModbus.HasValidCrc(bytes));
        Assert.Equal(bytes, WaveshareModbus.WithCrc(bytes.AsSpan(0, bytes.Length - 2)));
    }

    [Fact]
    public void Should_BuildTheWikisRequests_ByteForByte()
    {
        Assert.Equal("01 01 00 00 00 08 3D CC", WaveshareModbus.Hex(WaveshareModbus.ReadCoils(1, 0, 8).Frame));
        Assert.Equal("01 02 00 00 00 08 79 CC", WaveshareModbus.Hex(WaveshareModbus.ReadDiscreteInputs(1, 0, 8).Frame));
        Assert.Equal("01 05 00 00 FF 00 8C 3A", WaveshareModbus.Hex(WaveshareModbus.WriteSingleCoil(1, 0, true).Frame));
        Assert.Equal("01 05 00 00 00 00 CD CA", WaveshareModbus.Hex(WaveshareModbus.WriteSingleCoil(1, 0, false).Frame));
        Assert.Equal("01 05 00 01 FF 00 DD FA", WaveshareModbus.Hex(WaveshareModbus.WriteSingleCoil(1, 1, true).Frame));
        Assert.Equal("01 03 80 00 00 01 AD CA", WaveshareModbus.Hex(WaveshareModbus.ReadHoldingRegisters(1, 0x8000, 1).Frame));
        Assert.Equal("01 03 40 00 00 01 91 CA", WaveshareModbus.Hex(WaveshareModbus.ReadHoldingRegisters(1, 0x4000, 1).Frame));
    }

    [Fact]
    public void Should_WriteAllEightModes_InOneFrame()
    {
        var request = WaveshareModbus.WriteMultipleRegisters(1, WaveshareApi.RelayModeRegister, new ushort[8]);

        Assert.Equal(17 + 8, request.Frame.Length);
        Assert.Equal("01 10 10 00 00 08 10", WaveshareModbus.Hex(request.Frame.AsSpan(0, 7)));
        Assert.True(WaveshareModbus.HasValidCrc(request.Frame));
    }

    [Theory]
    [InlineData("01 01 01 01 90 48", 0b0000_0001UL)]
    [InlineData("01 01 01 41 91 B8", 0b0100_0001UL)]
    [InlineData("01 01 01 00 51 88", 0UL)]
    public async Task Should_ReadTheWikisRelayAnswers(string answer, ulong relays)
    {
        var frame = await ReadAsync(WaveshareModbus.ReadCoils(1, 0, 8), answer);

        Assert.Equal(relays, WaveshareModbus.Bits(frame, 8));
    }

    [Fact]
    public async Task Should_ReadTheWikisInputAnswer()
    {
        var frame = await ReadAsync(WaveshareModbus.ReadDiscreteInputs(1, 0, 8), "01 02 01 00 A1 88");

        Assert.Equal(0UL, WaveshareModbus.Bits(frame, 8));
    }

    [Fact]
    public async Task Should_ReadThirtyRelays_AcrossFourBytes()
    {
        var answer = WaveshareModbus.WithCrc([1, 1, 4, 0x01, 0x00, 0x00, 0xE0]);

        var frame = await ReadAsync(WaveshareModbus.ReadCoils(1, 0, 30), WaveshareModbus.Hex(answer));

        // Bits past relay 30 are padding and ignored.
        Assert.Equal((1UL << 0) | (1UL << 29), WaveshareModbus.Bits(frame, 30));
    }

    [Fact]
    public async Task Should_TakeTheEcho_AsTheAnswerToASwitch()
    {
        var frame = await ReadAsync(WaveshareModbus.WriteSingleCoil(1, 0, true), "01 05 00 00 FF 00 8C 3A");

        Assert.Equal("01 05 00 00 FF 00 8C 3A", WaveshareModbus.Hex(frame));
    }

    [Fact]
    public async Task Should_ThrowRefused_ForAnExceptionAnswer()
    {
        var error = await Assert.ThrowsAsync<WaveshareRefusedException>(() => ReadAsync(WaveshareModbus.WriteSingleCoil(1, 0, true), "01 85 03 02 91"));

        Assert.Equal(3, error.Code);
        Assert.Equal("The board refused it: a value it doesn't take (code 3).", error.Message);
    }

    [Theory]
    [InlineData("01 01 01 01 90 49", "The answer's CRC is wrong.")]
    [InlineData("02 01 01 01 90 48", "The answer came from unit 2, not 1.")]
    [InlineData("01 02 01 01 90 48", "The answer is for function 02, not 01.")]
    [InlineData("01 01 02 01 00 00 00", "The answer has 2 bytes of data, not 1.")]
    [InlineData("01 01 FF 00", "The answer has 255 bytes of data, not 1.")]
    [InlineData("01 81 02 00 00", "The answer's CRC is wrong.")]
    public async Task Should_RefuseAnAnswerThatIsntTheAnswer(string answer, string message)
    {
        var error = await Assert.ThrowsAsync<WaveshareProtocolException>(() => ReadAsync(WaveshareModbus.ReadCoils(1, 0, 8), answer));

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("01 05 00 01 FF 00 DD FA")]
    [InlineData("01 05 00 00 00 00 CD CA")]
    public async Task Should_RefuseAnEchoOfAnotherRequest(string answer)
    {
        var error = await Assert.ThrowsAsync<WaveshareProtocolException>(() => ReadAsync(WaveshareModbus.WriteSingleCoil(1, 0, true), answer));

        Assert.Equal("The answer doesn't confirm what was asked.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("01 01 01 01 90")]
    [InlineData("01 05 00 00 FF")]
    public async Task Should_ThrowIo_WhenTheAnswerStopsShort(string answer)
    {
        var request = answer.StartsWith("01 05", StringComparison.Ordinal) ? WaveshareModbus.WriteSingleCoil(1, 0, true) : WaveshareModbus.ReadCoils(1, 0, 8);

        await Assert.ThrowsAsync<IOException>(() => ReadAsync(request, answer));
    }

    [Fact]
    public async Task Should_ReadNoMoreThanTheAnswer_WhenMoreFollows()
    {
        using var stream = new MemoryStream([.. Bytes("01 01 01 01 90 48"), .. new byte[1024 * 1024]]);

        var frame = await WaveshareModbus.ReadAnswerAsync(stream, WaveshareModbus.ReadCoils(1, 0, 8), Cancel);

        Assert.Equal(6, frame.Length);
        Assert.Equal(6, stream.Position);
    }

    [Fact]
    public void Should_ReadRegisters()
    {
        var answer = WaveshareModbus.WithCrc([1, 3, 4, 0x00, 0x01, 0x00, 0x03]);

        Assert.Equal(new ushort[] { 1, 3 }, WaveshareModbus.Registers(answer));
        Assert.Throws<WaveshareProtocolException>(() => WaveshareModbus.Registers(WaveshareModbus.WithCrc([1, 3, 3, 0, 1, 0])));
    }

    [Fact]
    public void Should_RefuseQuantitiesModbusDoesntTake()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveshareModbus.ReadCoils(1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveshareModbus.ReadCoils(1, 0, 2001));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveshareModbus.ReadHoldingRegisters(1, 0, 126));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveshareModbus.WriteMultipleRegisters(1, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => WaveshareModbus.Bits(Bytes("01 01 01 01 90 48"), 65));
    }

    internal static byte[] Bytes(string hex)
    {
        return hex.Length == 0 ? [] : Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
    }

    private static async Task<byte[]> ReadAsync(WaveshareRequest request, string answer)
    {
        using var stream = new MemoryStream(Bytes(answer));
        return await WaveshareModbus.ReadAnswerAsync(stream, request, Cancel);
    }
}
