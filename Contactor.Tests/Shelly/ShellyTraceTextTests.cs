using System.Text;
using Contactor.Boards;
using Contactor.Boards.Shelly;

namespace Contactor.Tests.Shelly;

/// <summary>The Shelly's requests and answers in plain words.</summary>
public sealed class ShellyTraceTextTests
{
    [Theory]
    [InlineData(ShellyApi.GetDeviceInfo, 0, false, "Ask which device it is")]
    [InlineData(ShellyApi.GetStatus, 0, false, "Ask for status")]
    [InlineData(ShellyApi.SwitchSet, 3, true, "Turn on relay 3")]
    [InlineData(ShellyApi.SwitchSet, 1, false, "Turn off relay 1")]
    [InlineData("Switch.Toggle", 0, false, "Call Switch.Toggle")]
    [InlineData("Bad\nname", 0, false, "Call Bad·name")]
    public void Should_SayWhatARequestAsksFor(string method, int relay, bool on, string text)
    {
        Assert.Equal(text, ShellyTraceText.Request(method, relay, on));
    }

    [Theory]
    [InlineData(0b0000, 4, "Status: all off")]
    [InlineData(0b0101, 4, "Status: relay 1 and 3 on")]
    [InlineData(0b1111, 4, "Status: all on")]
    [InlineData(0b0001, 1, "Status: relay 1 on")]
    [InlineData(0b0111, 4, "Status: relay 1, 2 and 3 on")]
    public void Should_SayTheStatus(byte relays, int count, string text)
    {
        Assert.Equal(text, ShellyTraceText.Status(new ShellyStatus(relays), count));
    }

    [Fact]
    public void Should_ShowTheRequest_WithoutTheDigest()
    {
        var frame = ShellyJson.WriteSwitchSet(4, relay: 2, on: true);

        Assert.Equal("POST /rpc {\"id\":4,\"src\":\"contactor\",\"method\":\"Switch.Set\",\"params\":{\"id\":1,\"on\":true}}", ShellyTraceText.RequestBytes(frame, withDigest: false));
        Assert.EndsWith(" · with the password's digest", ShellyTraceText.RequestBytes(frame, withDigest: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Should_CutLongAnswers_AndHideWhatIsntPrintable()
    {
        var text = ShellyTraceText.Answer(200, "{\"a\":\"\u0001\"}" + new string('x', 500));

        Assert.StartsWith("200 {\"a\":\"·\"}", text, StringComparison.Ordinal);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
        Assert.Equal("401", ShellyTraceText.Answer(401, ""));
        Assert.True(Encoding.UTF8.GetByteCount(text) < 300);
    }

    [Fact]
    public void Should_DescribeTheDevice()
    {
        Assert.Equal(
            "Device SPSW-004PE16EU, generation 2, firmware 1.7.1, profile switch",
            ShellyTraceText.DeviceInfo(new ShellyDeviceInfo("shellypro4pm-f008d1d8b8b8", "SPSW-004PE16EU", 2, "1.7.1", "switch")));
        Assert.Equal(
            "Device shelly1-x, no generation, firmware unknown",
            ShellyTraceText.DeviceInfo(new ShellyDeviceInfo("shelly1-x", null, null, null, null)));
        Assert.Equal("Relay 2 is on (it was off)", ShellyTraceText.Switched(2, true, false));
        Assert.Equal("Relay 1 is off", ShellyTraceText.Switched(1, false, null));
    }

    [Fact]
    public void Should_DescribeThePowerOnSettings()
    {
        Assert.Equal("Ask for relay 2's settings", ShellyTraceText.Request(ShellyApi.SwitchGetConfig, 2));
        Assert.Equal("Call Switch.GetConfig", ShellyTraceText.Request(ShellyApi.SwitchGetConfig));
        Assert.Equal("Set relay 3 after a power cut: follows its switch", ShellyTraceText.SetPowerOn(3, PowerOnState.FollowsInput));
        Assert.Equal("Relay 1 after a power cut: as before", ShellyTraceText.PowerOnRead(1, PowerOnState.AsBefore));
        Assert.Equal("Relay 4 after a power cut: unknown", ShellyTraceText.PowerOnRead(4, null));
        Assert.Equal("Relay 2's setting saved", ShellyTraceText.SettingSaved(2));
        Assert.Equal("Settings not read from relay 2 on: unknown after a power cut", ShellyTraceText.PowerOnReadStopped(2));
        Assert.Equal(["off", "as before", "on", "follows its switch"], Enum.GetValues<PowerOnState>().Select(s => ShellyTraceText.PowerOn(s)));
    }
}
