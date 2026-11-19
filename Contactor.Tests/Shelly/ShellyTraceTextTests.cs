using System.Text;
using Contactor.Boards.Shelly;

namespace Contactor.Tests.Shelly;

/// <summary>The Shelly's requests and answers in plain words.</summary>
public sealed class ShellyTraceTextTests
{
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
}
