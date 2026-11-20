using System.Text;
using Contactor.Boards.Velleman;

namespace Contactor.Tests.Velleman;

/// <summary>The board's JSON answers: what the firmware writes is read, anything else refused, never half-read.</summary>
public sealed class Vm204JsonTests
{
    // Exactly as createJsonData (Main/main.c) writes it, spaces and all.
    private const string FirmwareStatus = "{\"relays\": [true,false,true,false],\"pulse\": [false,false,false,false],\"inputs\": [false,false,false,true],\"analog\": 1023}";

    [Fact]
    public void Should_ReadTheStatus_AsTheFirmwareWritesIt()
    {
        Assert.Equal(new Vm204Status(0b0101, 0b1000, 1023, Pulses: 0), Vm204Json.ReadStatus(Bytes(FirmwareStatus)));
        Assert.Equal((byte)0b0110, Vm204Json.ReadStatus(Bytes(FirmwareStatus.Replace("\"pulse\": [false,false,false,false]", "\"pulse\": [false,true,true,false]", StringComparison.Ordinal))).Pulses);
    }

    // Only the pulse command reads the flags, and it pulses nothing while they are unknown: never an unreadable status.
    [Theory]
    [InlineData("{\"relays\":[true,false,false,false],\"inputs\":[false,false,false,false]}")]
    [InlineData("{\"relays\":[true,false,false,false],\"pulse\":[1,0,0,0],\"inputs\":[false,false,false,false]}")]
    [InlineData("{\"relays\":[true,false,false,false],\"pulse\":[false,false],\"inputs\":[false,false,false,false]}")]
    public void Should_ReadUnknownPulses_WhenTheFlagsArentFourTrueOrFalse(string json)
    {
        Assert.Equal(new Vm204Status(0b0001, 0, null, Pulses: null), Vm204Json.ReadStatus(Bytes(json)));
    }

    [Fact]
    public void Should_WriteTheStatus_AsTheFirmwareDoes()
    {
        Assert.Equal(FirmwareStatus, Vm204Json.WriteStatus(new Vm204Status(0b0101, 0b1000, 1023)));
    }

    [Theory]
    [InlineData("{\"relays\":[false,false,false,false],\"inputs\":[false,false,false,false]}")]
    [InlineData("{\"relays\":[false,false,false,false],\"inputs\":[false,false,false,false],\"analog\":\"high\"}")]
    [InlineData("{\"relays\":[false,false,false,false],\"inputs\":[false,false,false,false],\"analog\":1.5}")]
    [InlineData("{\"relays\":[false,false,false,false],\"inputs\":[false,false,false,false],\"analog\":99999999999}")]
    public void Should_ReadNoAnalogValue_WhenItIsMissingOrNotAWholeNumber(string json)
    {
        Assert.Null(Vm204Json.ReadStatus(Bytes(json)).Analog);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"relays\": [true,false,true,false]")]
    [InlineData("{\"relays\": [true,false,true],\"inputs\": [false,false,false,false]}")]
    [InlineData("{\"relays\": [true,false,true,false,true],\"inputs\": [false,false,false,false]}")]
    [InlineData("{\"relays\": [1,0,1,0],\"inputs\": [false,false,false,false]}")]
    [InlineData("{\"relays\": [\"true\",false,true,false],\"inputs\": [false,false,false,false]}")]
    [InlineData("{\"relays\": [true,false,true,false],\"inputs\": null}")]
    [InlineData("{\"Relays\": [true,false,true,false],\"inputs\": [false,false,false,false]}")]
    [InlineData("{\"relays\": [true,false,true,false],\"inputs\": [[false],false,false,false]}")]
    [InlineData("ÿþ garbage")]
    public void Should_RefuseAStatus_ThatIsNotWhatTheBoardSends(string json)
    {
        Assert.Throws<Vm204ProtocolException>(() => Vm204Json.ReadStatus(Bytes(json)));
    }

    [Fact]
    public void Should_RefuseDeepNesting_AndAnAnswerOverTheLimit()
    {
        var deep = new string('[', 1000) + new string(']', 1000);
        var huge = "{\"relays\": [true,false,true,false],\"inputs\": [false,false,false,false],\"x\":\"" + new string('a', Vm204Api.MaxResponseBytes) + "\"}";

        Assert.Throws<Vm204ProtocolException>(() => Vm204Json.ReadStatus(Bytes(deep)));
        Assert.Throws<Vm204ProtocolException>(() => Vm204Json.ReadStatus(Bytes(huge)));
    }

    [Fact]
    public void Should_ReadTheKey_AsTheFirmwareWritesIt()
    {
        Assert.Equal("Ab12", Vm204Json.ReadKey(Bytes("{\"ApiKey\":\"Ab12\"}")));
        Assert.Equal("{\"ApiKey\":\"Ab12\"}", Vm204Json.WriteKey("Ab12"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"ApiKey\":\"\"}")]
    [InlineData("{\"ApiKey\":12}")]
    [InlineData("{\"ApiKey\":\"a&relay=1\"}")]
    [InlineData("{\"ApiKey\":\"åäö\"}")]
    [InlineData("{\"apikey\":\"Ab12\"}")]
    public void Should_RefuseAKey_ThatCantBeSentBack(string json)
    {
        Assert.Throws<Vm204ProtocolException>(() => Vm204Json.ReadKey(Bytes(json)));
    }

    [Fact]
    public void Should_RefuseAKey_LongerThan32()
    {
        Assert.Equal(32, Vm204Json.ReadKey(Bytes($"{{\"ApiKey\":\"{new string('a', 32)}\"}}")).Length);
        Assert.Throws<Vm204ProtocolException>(() => Vm204Json.ReadKey(Bytes($"{{\"ApiKey\":\"{new string('a', 33)}\"}}")));
    }

    // parseRelayAPICommand, parsePulseAPICommand, isAPIAuthorized and authUser strcpy the query into 64 bytes (Main/api.c).
    [Fact]
    public void Should_FitAQuery_OnlyUpTo63Characters()
    {
        Assert.True(Vm204Api.QueryFits($"/api/relay/pulse?{new string('a', 63)}"));
        Assert.False(Vm204Api.QueryFits($"/api/relay/pulse?{new string('a', 64)}"));
        Assert.True(Vm204Api.QueryFits("/api/status"));
    }

    private static byte[] Bytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }
}
