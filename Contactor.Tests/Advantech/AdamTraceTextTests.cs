using Contactor.Boards.Advantech;

namespace Contactor.Tests.Advantech;

/// <summary>The ADAM modules' Modbus TCP frames in plain words, as the Debug screen and the simulator show them.</summary>
public sealed class AdamTraceTextTests
{
    [Theory]
    [InlineData("00 01 00 00 00 06 01 01 00 10 00 06", "Ask for the relays")]
    [InlineData("00 01 00 00 00 06 01 02 00 00 00 06", "Ask for the inputs")]
    [InlineData("00 02 00 00 00 06 01 05 00 12 FF 00", "Turn on relay 3")]
    [InlineData("00 02 00 00 00 06 01 05 00 15 00 00", "Turn off relay 6")]
    [InlineData("00 02 00 00 00 06 01 05 00 10 FF FF", "Write relay 1 with FFFF")]
    [InlineData("00 02 00 00 00 06 01 05 00 03 FF 00", "Function 05 at 0003")]
    [InlineData("00 03 00 00 00 08 01 0F 00 10 00 03 01 05", "Set 3 relays from relay 1")]
    [InlineData("00 01 00 00 00 06 01 01 00 17 00 08", "Function 01 at 0017")]
    [InlineData("00 04 00 00 00 06 01 03 00 01 00 02", "Function 03 at 0001")]
    [InlineData("00 01 00 00 00 06 01 01", "A frame too short to be a request")]
    [InlineData("", "A frame too short to be a request")]
    public void Should_SayWhatARequestAsks(string frame, string text)
    {
        Assert.Equal(text, AdamTraceText.Request(AdamModbusTests.Bytes(frame)));
    }

    [Theory]
    [InlineData(0UL, 6, "Relays: all off")]
    [InlineData(0x3FUL, 6, "Relays: all on")]
    [InlineData(0b0100UL, 6, "Relays: 3 on")]
    [InlineData(0b0101UL, 6, "Relays: 1 and 3 on")]
    [InlineData(0b1101UL, 6, "Relays: 1, 3 and 4 on")]
    [InlineData(1UL << 5, 6, "Relays: 6 on")]
    [InlineData(1UL << 6, 6, "Relays: all off")]
    public void Should_SayWhichRelaysAreOn(ulong relays, int count, string text)
    {
        Assert.Equal(text, AdamTraceText.Relays(relays, count));
    }

    [Theory]
    [InlineData(0UL, 6, "Inputs: all off")]
    [InlineData(0b10UL, 6, "Inputs: 2 on")]
    [InlineData(0x3FUL, 6, "Inputs: all on")]
    public void Should_SayWhichInputsAreOn(ulong inputs, int count, string text)
    {
        Assert.Equal(text, AdamTraceText.Inputs(inputs, count));
    }

    [Theory]
    [InlineData("00 02 00 00 00 06 01 05 00 12 FF 00", "Relay 3 turned on")]
    [InlineData("00 02 00 00 00 06 01 05 00 12 00 00", "Relay 3 turned off")]
    [InlineData("00 02 00 00 00 06 01 05 00 03 FF 00", "Done")]
    [InlineData("00 05 00 00 00 06 01 0F 00 10 00 03", "Done")]
    [InlineData("00 05", "Done")]
    public void Should_SayWhatAnAnswerConfirms(string frame, string text)
    {
        Assert.Equal(text, AdamTraceText.Confirmed(AdamModbusTests.Bytes(frame)));
    }

    [Theory]
    [InlineData(1, "Refused: unknown function (code 1)")]
    [InlineData(2, "Refused: no such address (code 2)")]
    [InlineData(3, "Refused: a value it doesn't take (code 3)")]
    [InlineData(4, "Refused: it failed (code 4)")]
    [InlineData(11, "Refused: exception (code 11)")]
    public void Should_NameTheExceptionCodes(byte code, string text)
    {
        Assert.Equal(text, AdamTraceText.Refused(code));
    }
}
