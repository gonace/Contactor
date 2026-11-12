using Contactor.Boards.Vm208;

namespace Contactor.Tests.Vm208;

/// <summary>The VM208's requests and answers in plain words, and the relay form read as the firmware reads it.</summary>
public sealed class Vm208TraceTextTests
{
    [Theory]
    [InlineData("GET", "/status", null, "Ask for status")]
    [InlineData("POST", "/relay", "index=3&state=1&interface=-1&socket=0", "Turn on relay 3")]
    [InlineData("POST", "/relay", "index=2&state=0&interface=-1&socket=0", "Turn off relay 2")]
    [InlineData("POST", "/relay", "index=9&state=0&interface=-1&socket=0", "Ask for POST /relay")]
    [InlineData("GET", "/heap", null, "Ask for GET /heap")]
    public void Should_SayWhatARequestAsksFor(string method, string path, string? form, string text)
    {
        Assert.Equal(text, Vm208TraceText.Request(method, path, form));
    }

    [Fact]
    public void Should_ShowTheRequestsBytes()
    {
        Assert.Equal("GET /status?interface=-1&socket=0", Vm208TraceText.RequestBytes("GET", $"{Vm208Api.StatusPath}?{Vm208Api.StatusQuery}"));
        Assert.Equal("POST /relay index=4&state=0&interface=-1&socket=0", Vm208TraceText.RequestBytes("POST", Vm208Api.RelayPath, Vm208Api.RelayForm(4, false)));
    }

    [Theory]
    [InlineData(0b0000, false, 0, "Status: all off · input off")]
    [InlineData(0b0101, true, 0, "Status: relay 1 and 3 on · input on")]
    [InlineData(0b1111, false, 3, "Status: all on · input off · m1 and m2 on")]
    [InlineData(0b0010, false, 2, "Status: relay 2 on · input off · m2 on")]
    public void Should_PutTheStatusInWords(byte relays, bool input, byte outputs, string text)
    {
        Assert.Equal(text, Vm208TraceText.Status(new Vm208Status(relays, input, outputs)));
    }

    [Fact]
    public void Should_CutALongAnswer_AndHideWhatIsntPrintable()
    {
        var shown = Vm208TraceText.Answer(200, "{\"name\":\"a\u0001b\"}" + new string('x', 1000));

        Assert.StartsWith("200 {\"name\":\"a·b\"}", shown, StringComparison.Ordinal);
        Assert.EndsWith("…", shown, StringComparison.Ordinal);
        Assert.True(shown.Length < 320);
        Assert.Equal("404", Vm208TraceText.Answer(404, ""));
    }

    [Theory]
    [InlineData("index=1&state=1&interface=-1&socket=0", 1, true)]
    [InlineData("index=4&state=0&interface=-1&socket=0", 4, false)]
    [InlineData("relay=2&on=7&interface=-1&socket=9", 2, true)]
    [InlineData("index=3x&state=abc&interface=-1x&socket=0", 3, false)]
    [InlineData("index=%32&state=1&interface=%2D1&socket=0", 2, true)]
    public void Should_ReadTheRelayForm_ByPosition_AsTheFirmwareDoes(string form, int relay, bool turnOn)
    {
        Assert.Equal((relay, turnOn), Vm208TraceText.ReadRelayForm(form));
    }

    [Theory]
    [InlineData("")]
    [InlineData("index=1&state=1&interface=-1")]
    [InlineData("index=1&state=1&interface=-1&socket=0&x=1")]
    [InlineData("index=1&state=1&interface=0&socket=0")]
    [InlineData("index=0&state=1&interface=-1&socket=0")]
    [InlineData("index=5&state=1&interface=-1&socket=0")]
    [InlineData("index=99999999999999999999&state=1&interface=-1&socket=0")]
    public void Should_FindNoRelay_WhereTheFirmwareWouldCrashOrUseAnotherCard(string form)
    {
        Assert.Null(Vm208TraceText.ReadRelayForm(form));
    }

    [Fact]
    public void Should_RefuseToFormARelayTheBoardHasNot()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm208Api.RelayForm(0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vm208Api.RelayForm(5, true));
    }
}
