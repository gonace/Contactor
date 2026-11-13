using Contactor.Boards.Devantech;

namespace Contactor.Tests.Devantech;

/// <summary>The commands and answers in plain words, as the Debug screen and the simulator show them.</summary>
public sealed class DevantechTraceTextTests
{
    [Theory]
    [InlineData("10", "Ask which board it is")]
    [InlineData("24", "Ask for the relays")]
    [InlineData("25", "Ask for the inputs")]
    [InlineData("7A", "Ask whether the board is locked")]
    [InlineData("79 61 70 70 6C 65", "Send the password")]
    [InlineData("20 03 00", "Turn on relay 3")]
    [InlineData("21 08 00", "Turn off relay 8")]
    [InlineData("20 02 32", "Turn on relay 2 for 5.0 s")]
    [InlineData("20 03 05", "Turn on relay 3 for 0.5 s")]
    [InlineData("20 14 FF", "Turn on relay 20 for 25.5 s")]
    [InlineData("20 02", "Command 20")]
    [InlineData("3A", "Command 3A")]
    [InlineData("", "An empty command")]
    public void Should_SayWhatACommandAsks(string command, string text)
    {
        Assert.Equal(text, DevantechTraceText.Request(DevantechProtocolTests.Bytes(command)));
    }

    [Fact]
    public void Should_NeverShowThePassword()
    {
        Assert.Equal("79 …", DevantechTraceText.RequestBytes(DevantechProtocol.Password("apple").Bytes));
        Assert.Equal("20 03 00", DevantechTraceText.RequestBytes(DevantechProtocol.Switch(3, on: true).Bytes));
    }

    [Theory]
    [InlineData("13 02 1C", "Board: ETH008, hardware 2, firmware 28")]
    [InlineData("14 01 05", "Board: ETH484, hardware 1, firmware 5")]
    [InlineData("63 01 05", "Board: module 99, hardware 1, firmware 5")]
    public void Should_SayWhichBoardAnswered(string answer, string text)
    {
        Assert.Equal(text, DevantechTraceText.Module(DevantechProtocolTests.Bytes(answer)));
    }

    [Theory]
    [InlineData(0x00, "Locked: the board needs the password")]
    [InlineData(0x1E, "Unlocked for 30 s")]
    [InlineData(0x01, "Unlocked for 1 s")]
    [InlineData(0xFF, "No password")]
    [InlineData(0x40, "Unknown lock state 40")]
    public void Should_SayWhetherTheBoardIsLocked(byte answer, string text)
    {
        Assert.Equal(text, DevantechTraceText.UnlockTime(answer));
    }

    [Fact]
    public void Should_SayWhatTheAnswersMean()
    {
        Assert.Equal("Password accepted", DevantechTraceText.PasswordAnswer(1));
        Assert.Equal("Password refused", DevantechTraceText.PasswordAnswer(2));
        Assert.Equal("Relay 3 turned on", DevantechTraceText.Switched(DevantechProtocol.Switch(3, on: true).Bytes, 0));
        Assert.Equal("Relay 3 turned off", DevantechTraceText.Switched(DevantechProtocol.Switch(3, on: false).Bytes, 0));
        Assert.Equal("Refused: relay 9", DevantechTraceText.Switched(DevantechProtocol.Switch(9, on: true).Bytes, 1));
        Assert.Equal("Relay 3 turned on for 0.5 s", DevantechTraceText.Switched(DevantechProtocol.Pulse(3, TimeSpan.FromMilliseconds(500)).Bytes, 0));
        Assert.Equal("Refused: relay 3", DevantechTraceText.Switched(DevantechProtocol.Pulse(3, TimeSpan.FromMilliseconds(500)).Bytes, 1));
        Assert.Equal("Relays: 1, 3 and 20 on", DevantechTraceText.Relays(0b101 | (1UL << 19), 20));
        Assert.Equal("Relays: all on", DevantechTraceText.Relays(0b11, 2));
        Assert.Equal("Inputs: all off", DevantechTraceText.Inputs(0, 8));
    }
}
