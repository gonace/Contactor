using System.Globalization;
using Contactor.Boards.Devantech;

namespace Contactor.Tests.Devantech;

/// <summary>
/// The commands' bytes and the answers' layouts, against the exchanges in Devantech's documents (the ETH002-B and
/// ETH008 technical documents) and a session recorded on a real ETH008-B; the ETH8020 layout as Devantech's ETH8020
/// table gives it.
/// </summary>
public sealed class DevantechProtocolTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static byte[] Bytes(string hex)
    {
        return [.. hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(b => byte.Parse(b, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
    }

    [Fact]
    public void Should_BuildTheCommands_AsTheRecordedSessionSendsThem()
    {
        Assert.Equal("10", DevantechProtocol.Hex(DevantechProtocol.GetModuleInfo().Bytes));
        Assert.Equal(3, DevantechProtocol.GetModuleInfo().AnswerLength);
        Assert.Equal("24", DevantechProtocol.Hex(DevantechProtocol.GetOutputs(DevantechModels.Layout(DevantechModels.Eth008)).Bytes));
        Assert.Equal("20 08 00", DevantechProtocol.Hex(DevantechProtocol.Switch(8, on: true).Bytes));
        Assert.Equal("21 08 00", DevantechProtocol.Hex(DevantechProtocol.Switch(8, on: false).Bytes));
        Assert.Equal(1, DevantechProtocol.Switch(8, on: true).AnswerLength);
        Assert.Equal("7A", DevantechProtocol.Hex(DevantechProtocol.GetUnlockTime().Bytes));
    }

    [Fact]
    public void Should_SendThePasswordAsAscii_WithNothingElse()
    {
        // The documents' example: "apple".
        var request = DevantechProtocol.Password("apple");

        Assert.Equal("79 61 70 70 6C 65", DevantechProtocol.Hex(request.Bytes));
        Assert.Equal(1, request.AnswerLength);
        Assert.Throws<ArgumentException>(() => DevantechProtocol.Password(""));
        Assert.Throws<ArgumentException>(() => DevantechProtocol.Password("two words"));
        Assert.Throws<ArgumentException>(() => DevantechProtocol.Password("äpple"));
    }

    [Theory]
    [InlineData("devantech-eth002", 1, 0)]
    [InlineData("devantech-eth008", 1, 0)]
    [InlineData("devantech-eth484", 2, 2)]
    [InlineData("devantech-eth8020", 3, 4)]
    public void Should_AskForAsManyBytes_AsTheModelSends(string id, int outputs, int inputs)
    {
        var layout = DevantechModels.Layout(DevantechModels.All.Single(m => m.Id == id));

        Assert.Equal(outputs, DevantechProtocol.GetOutputs(layout).AnswerLength);
        if (inputs == 0)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => DevantechProtocol.GetInputs(layout));
        }
        else
        {
            Assert.Equal(inputs, DevantechProtocol.GetInputs(layout).AnswerLength);
        }
    }

    [Theory]
    [InlineData("0D", 8, 0b1101UL)]
    [InlineData("8D", 8, 0b1000_1101UL)]
    [InlineData("FF", 2, 0b11UL)]
    [InlineData("F3 FF", 4, 0b0011UL)]
    [InlineData("05 00 08", 20, (1UL << 0) | (1UL << 2) | (1UL << 19))]
    [InlineData("00 80 FF", 20, (1UL << 15) | 0xF_0000UL)]
    public void Should_ReadTheOutputs_LowByteFirst(string answer, int relays, ulong expected)
    {
        // The ETH008-B's 0D is relays 1, 3 and 4; the ETH484's second byte is its I/O, not a relay; the ETH8020's
        // relays 17–20 are the low half of the third byte.
        Assert.Equal(expected, DevantechProtocol.Outputs(Bytes(answer), relays));
    }

    [Theory]
    [InlineData("00 81", 0b1000_0001UL)]
    [InlineData("FF 01", 0b0000_0001UL)]
    [InlineData("00 00 00 81", 0b1000_0001UL)]
    [InlineData("FF FF FF 00", 0UL)]
    public void Should_ReadTheInputs_HighByteFirst(string answer, ulong expected)
    {
        Assert.Equal(expected, DevantechProtocol.Inputs(Bytes(answer), 8));
    }

    [Fact]
    public void Should_WriteTheAnswers_AsTheBoardSendsThem()
    {
        Assert.Equal("05 00 08", DevantechProtocol.Hex(DevantechProtocol.OutputBytes(0b0101 | (1UL << 19), 3)));
        Assert.Equal("00 00 00 81", DevantechProtocol.Hex(DevantechProtocol.InputBytes(0b1000_0001, 4)));
        Assert.Equal("00 81", DevantechProtocol.Hex(DevantechProtocol.InputBytes(0b1000_0001, 2)));
    }

    [Fact]
    public async Task Should_ReadExactlyTheAnswer_AndNoMore()
    {
        using var stream = new MemoryStream(Bytes("13 02 1C 0D"));

        Assert.Equal("13 02 1C", DevantechProtocol.Hex(await DevantechProtocol.ReadAnswerAsync(stream, 3, Cancel)));
        Assert.Equal(3, stream.Position);
        await Assert.ThrowsAsync<IOException>(() => DevantechProtocol.ReadAnswerAsync(stream, 2, Cancel));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DevantechProtocol.ReadAnswerAsync(stream, 5, Cancel));
    }

    [Fact]
    public void Should_RefuseRelayNumbers_ThatDontFitAByte()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DevantechProtocol.Switch(0, on: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => DevantechProtocol.Switch(256, on: true));
    }

    [Theory]
    [InlineData("devantech-eth008", 19, null)]
    [InlineData("devantech-eth008", 20, "The board at this address is an ETH484, not an ETH008.")]
    [InlineData("devantech-eth002", 19, "The board at this address is an ETH008, not an ETH002.")]
    [InlineData("devantech-eth8020", 52, "The board at this address is an ETH1610, not an ETH8020.")]
    [InlineData("devantech-eth484", 99, "The board at this address says it is module 99, not an ETH484.")]
    public void Should_SayWhichBoardIsThere_WhenItIsNotTheModel(string id, byte moduleId, string? message)
    {
        Assert.Equal(message, DevantechModels.CheckModule(DevantechModels.All.Single(m => m.Id == id), moduleId));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("apple", null)]
    [InlineData("A-b_c!9~", null)]
    [InlineData("12345678901234567890123456789012", null)]
    [InlineData("123456789012345678901234567890123", "The password can be at most 32 characters.")]
    [InlineData("two words", "The password can only contain letters a–z and A–Z, digits and plain symbols, without spaces.")]
    [InlineData("smörgås", "The password can only contain letters a–z and A–Z, digits and plain symbols, without spaces.")]
    public void Should_CheckThePassword(string password, string? message)
    {
        Assert.Equal(message, DevantechModels.CheckPassword(password));
    }
}
