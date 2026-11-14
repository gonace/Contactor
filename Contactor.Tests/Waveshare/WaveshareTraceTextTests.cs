using Contactor.Boards.Waveshare;

namespace Contactor.Tests.Waveshare;

/// <summary>The Modbus frames in plain words, as the Debug screen and the simulator show them.</summary>
public sealed class WaveshareTraceTextTests
{
    [Theory]
    [InlineData("01 01 00 00 00 08 3D CC", "Ask for the relays")]
    [InlineData("01 02 00 00 00 08 79 CC", "Ask for the inputs")]
    [InlineData("01 03 10 00 00 08 40 CC", "Ask for the relay modes")]
    [InlineData("01 05 00 02 FF 00 2D FA", "Turn on relay 3")]
    [InlineData("01 05 00 00 00 00 CD CA", "Turn off relay 1")]
    [InlineData("01 05 00 00 55 00 F2 9A", "Flip relay 1")]
    [InlineData("01 05 02 00 00 07 8D B0", "Pulse relay 1 for 0.7 s")]
    [InlineData("01 05 02 1D 7F FF 3C 04", "Pulse relay 30 for 3276.7 s")]
    [InlineData("01 05 04 00 00 07 8D 38", "Write coil 1025 with 0007")]
    [InlineData("01 03 80 00 00 01 AD CA", "Function 03 at 8000")]
    [InlineData("01 05", "A frame too short to be a request")]
    public void Should_SayWhatARequestAsks(string frame, string text)
    {
        Assert.Equal(text, WaveshareTraceText.Request(WaveshareModbusTests.Bytes(frame)));
    }

    [Fact]
    public void Should_SayWhatTheModeWritesDo()
    {
        Assert.Equal("Set every relay to normal mode", WaveshareTraceText.Request(WaveshareModbus.WriteMultipleRegisters(1, 0x1000, new ushort[8]).Frame));
        Assert.Equal("Set the relay modes", WaveshareTraceText.Request(WaveshareModbus.WriteMultipleRegisters(1, 0x1000, [1, 0]).Frame));
        Assert.Equal("Set relay 2 to linkage mode", WaveshareTraceText.Request(WaveshareModbus.WithCrc([1, 6, 0x10, 0x01, 0, 1])));
    }

    [Theory]
    [InlineData(0UL, 8, "Relays: all off")]
    [InlineData(0xFFUL, 8, "Relays: all on")]
    [InlineData(0b0100UL, 8, "Relays: 3 on")]
    [InlineData(0b0101UL, 8, "Relays: 1 and 3 on")]
    [InlineData(0b1101UL, 8, "Relays: 1, 3 and 4 on")]
    [InlineData(1UL << 29, 30, "Relays: 30 on")]
    public void Should_SayWhichRelaysAreOn(ulong relays, int count, string text)
    {
        Assert.Equal(text, WaveshareTraceText.Relays(relays, count));
    }

    [Fact]
    public void Should_NameTheModesThatArentNormal()
    {
        Assert.Equal("Relay modes: all normal", WaveshareTraceText.Modes(new ushort[8]));
        Assert.Equal("Relay modes: 3 linkage, 5 toggle, 8 edge", WaveshareTraceText.Modes([0, 0, 1, 0, 2, 0, 0, 3]));
        Assert.Equal("Relay modes: 1 unknown (9)", WaveshareTraceText.Modes([9]));
    }

    [Theory]
    [InlineData(1, "Refused: unknown function (code 1)")]
    [InlineData(2, "Refused: no such address (code 2)")]
    [InlineData(3, "Refused: a value it doesn't take (code 3)")]
    [InlineData(4, "Refused: it failed (code 4)")]
    [InlineData(11, "Refused: exception (code 11)")]
    public void Should_NameTheExceptionCodes(byte code, string text)
    {
        Assert.Equal(text, WaveshareTraceText.Refused(code));
    }
}
