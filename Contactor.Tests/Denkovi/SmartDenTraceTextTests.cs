using Contactor.Boards.Denkovi;

namespace Contactor.Tests.Denkovi;

/// <summary>The smartDEN's Modbus TCP frames in plain words, as the Debug screen and the simulator show them.</summary>
public sealed class SmartDenTraceTextTests
{
    [Theory]
    [InlineData("00 01 00 00 00 06 00 01 00 00 00 10", "Ask for the relays")]
    [InlineData("00 02 00 00 00 06 00 05 00 02 FF 00", "Turn on relay 3")]
    [InlineData("00 02 00 00 00 06 00 05 00 0F 00 00", "Turn off relay 16")]
    [InlineData("00 02 00 00 00 06 00 05 00 00 FF 02", "Flip relay 1")]
    [InlineData("00 02 00 00 00 06 00 05 00 00 12 34", "Write coil 1 with 1234")]
    [InlineData("00 05 00 00 00 06 00 06 00 09 00 1E", "Pulse relay 10 for 3.0 s")]
    [InlineData("00 05 00 00 00 06 00 06 00 0F FF FF", "Pulse relay 16 for 6553.5 s")]
    [InlineData("00 05 00 00 00 06 00 06 60 00 00 1E", "Function 06 at 6000")]
    [InlineData("00 03 00 00 00 08 00 0F 00 00 00 03 01 05", "Set 3 relays from relay 1")]
    [InlineData("00 04 00 00 00 06 00 03 61 00 00 03", "Function 03 at 6100")]
    [InlineData("00 01 00 00 00 06 00 01", "A frame too short to be a request")]
    [InlineData("", "A frame too short to be a request")]
    public void Should_SayWhatARequestAsks(string frame, string text)
    {
        Assert.Equal(text, SmartDenTraceText.Request(SmartDenModbusTests.Bytes(frame)));
    }

    [Theory]
    [InlineData(0UL, 16, "Relays: all off")]
    [InlineData(0xFFFFUL, 16, "Relays: all on")]
    [InlineData(0b0100UL, 16, "Relays: 3 on")]
    [InlineData(0b0101UL, 16, "Relays: 1 and 3 on")]
    [InlineData(0b1101UL, 16, "Relays: 1, 3 and 4 on")]
    [InlineData(1UL << 15, 16, "Relays: 16 on")]
    [InlineData(1UL << 16, 16, "Relays: all off")]
    public void Should_SayWhichRelaysAreOn(ulong relays, int count, string text)
    {
        Assert.Equal(text, SmartDenTraceText.Relays(relays, count));
    }

    [Theory]
    [InlineData("00 02 00 00 00 06 00 05 00 02 FF 00", "Relay 3 turned on")]
    [InlineData("00 02 00 00 00 06 00 05 00 02 00 00", "Relay 3 turned off")]
    [InlineData("00 02 00 00 00 06 00 05 00 02 FF 02", "Relay 3 flipped")]
    [InlineData("00 05 00 00 00 06 00 06 00 09 00 1E", "Relay 10 turned on for 3.0 s")]
    [InlineData("00 05 00 00 00 06 00 0F 00 00 00 03", "Done")]
    [InlineData("00 05", "Done")]
    public void Should_SayWhatAnAnswerConfirms(string frame, string text)
    {
        Assert.Equal(text, SmartDenTraceText.Confirmed(SmartDenModbusTests.Bytes(frame)));
    }

    [Theory]
    [InlineData(1, "Refused: unknown function (code 1)")]
    [InlineData(2, "Refused: no such address (code 2)")]
    [InlineData(3, "Refused: a value it doesn't take (code 3)")]
    [InlineData(4, "Refused: it failed (code 4)")]
    [InlineData(11, "Refused: exception (code 11)")]
    public void Should_NameTheExceptionCodes(byte code, string text)
    {
        Assert.Equal(text, SmartDenTraceText.Refused(code));
    }
}
