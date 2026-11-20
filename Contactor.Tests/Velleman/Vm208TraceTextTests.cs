using Contactor.Boards.Velleman;

namespace Contactor.Tests.Velleman;

/// <summary>The VM208's requests and answers in plain words, and the relay form read as the firmware reads it.</summary>
public sealed class Vm208TraceTextTests
{
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
